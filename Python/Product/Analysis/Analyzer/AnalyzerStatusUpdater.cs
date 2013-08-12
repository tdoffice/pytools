﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.PythonTools.Interpreter;

namespace Microsoft.PythonTools.Analysis.Analyzer {
    /// <summary>
    /// Structure representing an analysis operation's progress.
    /// </summary>
    public struct AnalysisProgress {
        /// <summary>
        /// The current progress of the operation.
        /// </summary>
        public int Progress;
        /// <summary>
        /// The value that <see cref="Progress"/> is approaching.
        /// </summary>
        public int Maximum;
        /// <summary>
        /// A message describing the current status, up to 100 characters long.
        /// </summary>
        public string Message;
    }

    /// <summary>
    /// The exception raised when an identifier is not unique. This typically
    /// indicates that the specified interpreter is already being analyzed.
    /// </summary>
    [Serializable]
    public class IdentifierInUseException : Exception {
        public IdentifierInUseException() { }
        public IdentifierInUseException(string message) : base(message) { }
        public IdentifierInUseException(string message, Exception inner) : base(message, inner) { }
        protected IdentifierInUseException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context)
            : base(info, context) { }
    }

    /// <summary>
    /// Provides interprocess communication for the analyzer so that progress
    /// updates can be sent to one or more VS processes.
    /// </summary>
    public class AnalyzerStatusUpdater : AnalyzerStatusUpdaterImplementation {
        /// <summary>
        /// Constructs an updater that will send events.
        /// </summary>
        /// <param name="identifier">
        /// A unique string that can be used by the receiver to identify the
        /// sender.
        /// </param>
        public AnalyzerStatusUpdater(string identifier)
            : base(identifier) {
        }

        /// <summary>
        /// Sends a status update.
        /// </summary>
        /// <param name="progress">
        /// A value less than or equal to <paramref name="maximum"/>
        /// </param>
        /// <param name="maximum">
        /// The value that <paramref name="progress"/> is approaching. This is
        /// permitted to vary during analysis.
        /// </param>
        public void UpdateStatus(int progress, int maximum, string message = null) {
            base.UpdateStatusImplementation(progress, maximum, message);
        }
    }

    /// <summary>
    /// Provides interprocess communication so that VS can receive progress
    /// updates from analyzers.
    /// </summary>
    public class AnalyzerStatusListener : AnalyzerStatusUpdaterImplementation {
        /// <summary>
        /// Constructs an updater that will receive events.
        /// </summary>
        /// <param name="callback">
        /// The function to call when updates are received. This is only raised
        /// in response to a call to <see cref="RequestUpdate"/>.
        /// </param>
        public AnalyzerStatusListener(Action<Dictionary<string, AnalysisProgress>> callback)
            : base(callback, TimeSpan.MaxValue) {
        }

        /// <summary>
        /// Constructs an updater that will receive events.
        /// </summary>
        /// <param name="callback">
        /// The function to call when updates are received. This is only raised
        /// in response to a call to <see cref="RequestUpdate"/>.
        /// </param>
        /// <param name="period">
        /// Call <see cref="RequestUpdate"/> repeatedly at this interval.
        /// </param>
        public AnalyzerStatusListener(Action<Dictionary<string, AnalysisProgress>> callback, TimeSpan period)
            : base(callback, period) {
        }

        /// <summary>
        /// Requests an update.
        /// </summary>
        /// <exception cref="InvalidOperationException">When an identifier
        /// string was provided to the constructor.</exception>
        public void RequestUpdate() {
            base.RequestUpdateImplementation();
        }

        /// <summary>
        /// Instructs an analysis operation to cancel itself.
        /// </summary>
        /// <remarks>
        /// This is not currently implemented or supported.
        /// </remarks>
        /// <param name="identifier">
        /// The identifier of the operation to cancel.
        /// </param>
        /// <exception cref="InvalidOperationException">When an identifier
        /// string was provided to the constructor.</exception>
        /// <exception cref="NotImplementedException">Always.</exception>
        public void RequestCancellation(string identifier) {
            base.RequestCancellationImplementation(identifier);
        }
    }

    /// <summary>
    /// Internal infrastructure. Use either <see cref="AnalyzerStatusUpdater"/>
    /// or <see cref="AnalyzerStatusListener"/>.
    /// </summary>
    public class AnalyzerStatusUpdaterImplementation : IDisposable {
        readonly Thread _worker;
        readonly string _identifier;
        readonly TimeSpan _period;

        readonly object _requestLock = new object();
        readonly Action<Dictionary<string, AnalysisProgress>> _callback;
        readonly object _latestUpdateLock = new object();
        AnalysisProgress _latestUpdate;
        volatile bool _abort;
        readonly AutoResetEvent _newRequest;
        readonly ManualResetEvent _workerStarted;

        Exception _pending;

        bool _disposed;

        const int LOCK_TIMEOUT = 1000;
        const int MAX_ITEMS = 128;
        static readonly string MUTEX_NAME = "Microsoft.PythonTools.AnalyzerStatus.Mutex." + AssemblyVersionInfo.Version;
        static readonly string MMF_NAME = "Microsoft.PythonTools.AnalyzerStatus.File." + AssemblyVersionInfo.Version;
        const int MAX_IDENTIFIER_LENGTH = 249;
        internal const int MAX_MESSAGE_LENGTH = 99;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        unsafe struct Data {
            public fixed char _Identifier[MAX_IDENTIFIER_LENGTH + 1];
            public bool Initialized;
            public bool InUse;
            public int OwnerProcessId;
            public int ItemsInQueue;
            public int MaximumItems;
            public fixed char _Message[MAX_MESSAGE_LENGTH + 1];

            public string Identifier {
                get {
                    unsafe {
                        fixed (char* id = _Identifier) {
                            return new string(id);
                        }
                    }
                }
                set {
                    if (value != null && value.Length > MAX_IDENTIFIER_LENGTH) {
                        throw new ArgumentException(string.Format("Identifier must be less than {0} characters", MAX_IDENTIFIER_LENGTH + 1));
                    }

                    unsafe {
                        fixed (char* id = _Identifier) {
                            if (value == null) {
                                id[0] = '\0';
                            } else {
                                char* p = id;
                                foreach (var c in value) {
                                    *p++ = c;
                                }
                                *p = '\0';
                            }
                        }
                    }
                }
            }

            public string Message {
                get {
                    unsafe {
                        fixed (char* msg = _Message) {
                            return new string(msg);
                        }
                    }
                }
                set {
                    var actualMessage = value;
                    if (actualMessage != null && actualMessage.Length > MAX_MESSAGE_LENGTH) {
                        actualMessage = value.Substring(0, MAX_MESSAGE_LENGTH - 3) + "...";
                    }

                    unsafe {
                        fixed (char* msg = _Message) {
                            if (actualMessage == null) {
                                msg[0] = '\0';
                            } else {
                                char* p = msg;
                                foreach (var c in actualMessage) {
                                    *p++ = c;
                                }
                                *p = '\0';
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets a unique identifier for the factory.
        /// </summary>
        public static string GetIdentifier(IPythonInterpreterFactory factory) {
            return GetIdentifier(factory.Id, factory.Configuration.Version);
        }

        /// <summary>
        /// Gets a unique identifier for the factory.
        /// </summary>
        public static string GetIdentifier(Guid id, Version version) {
            return string.Format(CultureInfo.InvariantCulture, "{0};{1}", id, version);
        }

        internal AnalyzerStatusUpdaterImplementation(Action<Dictionary<string, AnalysisProgress>> callback, TimeSpan period) {
            _callback = callback;
            _identifier = null;
            _period = period;
            _newRequest = new AutoResetEvent(false);
            _workerStarted = new ManualResetEvent(false);
            _worker = new Thread(ThreadProc);
            _worker.Name = "AnalyzerStatusUpdater Listener";
            _worker.IsBackground = true;
            _worker.Start();
        }

        internal AnalyzerStatusUpdaterImplementation(string identifier) {
            _identifier = identifier;
            _newRequest = new AutoResetEvent(false);
            _workerStarted = new ManualResetEvent(false);
            _worker = new Thread(ThreadProc);
            _worker.Name = "AnalyzerStatusUpdater Identifier=" + identifier;
            _worker.IsBackground = true;
            _worker.Start();
        }

        /// <summary>
        /// Waits for the worker to be fully initialized and running. To
        /// determine whether an exception was raised on initialization, call
        /// this function before <see cref="ThrowPendingExceptions"/>.
        /// </summary>
        public void WaitForWorkerStarted() {
            _workerStarted.WaitOne();
        }

        /// <summary>
        /// Raises any exceptions that occurred on the worker thread. This
        /// function should be called occasionally.
        /// </summary>
        public void ThrowPendingExceptions() {
            var exc = Interlocked.Exchange(ref _pending, null);
            if (exc != null) {
                throw exc;
            }
        }

        protected void UpdateStatusImplementation(int progress, int maximum, string message) {
            if (_identifier == null) {
                throw new InvalidOperationException("Cannot update status without providing an identifier");
            }

            lock (_latestUpdateLock) {
                _latestUpdate = new AnalysisProgress {
                    Progress = progress,
                    Maximum = maximum,
                    Message = message
                };
            }
            _newRequest.Set();
        }

        protected void RequestUpdateImplementation() {
            if (_identifier != null) {
                throw new InvalidOperationException("Cannot request updates when an identifier has been provided");
            }
            _newRequest.Set();
        }

        protected void RequestCancellationImplementation(string identifier) {
            if (_identifier != null) {
                throw new InvalidOperationException("Cannot request cancellation when an identifier has been provided");
            }
            throw new NotImplementedException();
        }

        /// <summary>
        /// <para>Closes the updater/listener, but does not block.</para>
        /// <para>There is no need to call this when calling
        /// <see cref="Dispose"/>, however, Dispose will block until the worker
        /// thread has ended.</para>
        /// </summary>
        public void Abort() {
            _abort = true;
            _newRequest.Set();
        }

        void ThreadProc() {
            Data me;
            Mutex globalLock;
            MemoryMappedFile sharedData;
            long dataOffset;

            int SIZE = Marshal.SizeOf(typeof(Data));
            int knownEntries = 0;

            try {
                Initialize(_identifier, out globalLock, out sharedData, out dataOffset, out me);
            } catch (Exception ex) {
                _pending = ex;
                return;
            } finally {
                _workerStarted.Set();
            }

            Dictionary<string, AnalysisProgress> response = null;

            for (; ; ) {
                if (_identifier == null && _period.TotalDays < 1) {
                    _newRequest.WaitOne((int)_period.TotalMilliseconds);
                } else {
                    _newRequest.WaitOne();
                }
                if (_abort) {
                    break;
                }

                try {
                    if (!globalLock.WaitOne(LOCK_TIMEOUT)) {
                        continue;
                    }
                } catch (AbandonedMutexException) {
                    break;
                }

                try {
                    if (_identifier != null) {
                        AnalysisProgress update;
                        lock (_latestUpdateLock) {
                            update = _latestUpdate;
                        }

                        using (var accessor = sharedData.CreateViewAccessor(dataOffset, SIZE)) {
                            me.ItemsInQueue = update.Progress;
                            me.MaximumItems = update.Maximum;
                            me.Message = update.Message;
                            accessor.Write(0, ref me);
                        }
                    } else {
                        response = ReadAllStatuses(sharedData, ref knownEntries);
                    }
                } finally {
                    globalLock.ReleaseMutex();
                }

                // Send the response outside the lock.
                if (response != null && _callback != null) {
                    _callback(response);
                    response = null;
                }
            }

            try {
                Uninitialize(_identifier, globalLock, sharedData, dataOffset);
            } catch (Exception ex) {
                _pending = ex;
            }
        }

        private Dictionary<string, AnalysisProgress> ReadAllStatuses(MemoryMappedFile sharedData, ref int knownEntries) {
            Data me = new Data { Initialized = false };
            int SIZE = Marshal.SizeOf(typeof(Data));

            var response = new Dictionary<string, AnalysisProgress>();
            using (var accessor = sharedData.CreateViewAccessor(0, (knownEntries + 1) * SIZE)) {
                for (int i = 0; i <= knownEntries; ++i) {
                    accessor.Read(i * SIZE, out me);
                    if (me.InUse) {
                        try {
                            var owner = Process.GetProcessById(me.OwnerProcessId);
                            response[me.Identifier] = new AnalysisProgress {
                                Progress = me.ItemsInQueue,
                                Maximum = me.MaximumItems,
                                Message = me.Message
                            };
                        } catch (InvalidOperationException) {
                        } catch (ArgumentException) {
                            // Process has died
                            me.InUse = false;
                            accessor.Write(i * SIZE, ref me);
                        }
                    }
                }
            }

            // If the last entry was initialized, there may be more
            // following it, so we keep reading.
            while (me.Initialized) {
                knownEntries += 1;
                using (var accessor = sharedData.CreateViewAccessor(knownEntries * SIZE, SIZE)) {
                    accessor.Read(0, out me);
                    if (me.InUse) {
                        try {
                            var owner = Process.GetProcessById(me.OwnerProcessId);
                            response[me.Identifier] = new AnalysisProgress {
                                Progress = me.ItemsInQueue,
                                Maximum = me.MaximumItems,
                                Message = me.Message
                            };
                        } catch (InvalidOperationException) {
                        } catch (ArgumentException) {
                            // Process has died
                            me.InUse = false;
                            accessor.Write(0, ref me);
                        }
                    }
                }
            }

            return response;
        }

        static void Initialize(string identifier, out Mutex mutex, out MemoryMappedFile file, out long offset, out Data data) {
            data = new Data {
                Identifier = identifier,
                Initialized = true,
                InUse = true,
                OwnerProcessId = Process.GetCurrentProcess().Id,
                ItemsInQueue = int.MaxValue,
                MaximumItems = 0,
                Message = null
            };
            Data empty = new Data {
                Identifier = string.Empty,
                Initialized = false,
                InUse = false
            };
            int SIZE = Marshal.SizeOf(data);

            bool isNew;
            try {
                mutex = new Mutex(true, MUTEX_NAME, out isNew);
            } catch (UnauthorizedAccessException) {
                mutex = Mutex.OpenExisting(MUTEX_NAME);
                isNew = false;
            }
            offset = 0;

            if (!isNew && !mutex.WaitOne(LOCK_TIMEOUT)) {
                mutex.Dispose();
                throw new InvalidOperationException("Unable to initialize inter-process communication");
            }

            file = null;
            try {
                if (isNew) {
                    file = MemoryMappedFile.CreateNew(MMF_NAME, (MAX_ITEMS + 2) * SIZE,
                        MemoryMappedFileAccess.ReadWrite,
                        MemoryMappedFileOptions.None,
                        null,
                        System.IO.HandleInheritability.None);

                    if (identifier != null) {
                        using (var accessor = file.CreateViewAccessor(offset, SIZE * 2)) {
                            accessor.Write(0, ref data);
                            // Zero the following entry
                            accessor.Write(SIZE, ref empty);
                        }
                    } else {
                        using (var accessor = file.CreateViewAccessor(offset, SIZE)) {
                            // Zero the first entry
                            accessor.Write(0, ref empty);
                        }
                    }
                } else {
                    file = MemoryMappedFile.OpenExisting(MMF_NAME, MemoryMappedFileRights.ReadWrite);

                    if (identifier != null) {
                        bool succeeded = false;
                        for (int index = 0; index < MAX_ITEMS; ++index) {
                            Data existing;
                            offset = index * SIZE;
                            using (var accessor = file.CreateViewAccessor(offset, SIZE * 2)) {
                                accessor.Read(0, out existing);
                                if (!existing.Initialized) {
                                    accessor.Write(0, ref data);
                                    // This wasn't initialized, so zero the following entry
                                    accessor.Write(SIZE, ref empty);
                                    succeeded = true;
                                    break;
                                } else if (!existing.InUse) {
                                    accessor.Write(0, ref data);
                                    // Initialized is true, so the next entry is zeroed already
                                    succeeded = true;
                                    break;
                                } else if (existing.Identifier == identifier) {
                                    throw new IdentifierInUseException("Identifier is already in use");
                                }
                            }
                        }
                        if (!succeeded) {
                            throw new InvalidOperationException("Out of space for entries");
                        }
                    }
                }
            } catch {
                mutex.ReleaseMutex();
                mutex.Dispose();
                mutex = null;
                if (file != null) {
                    file.Dispose();
                    file = null;
                }
                throw;
            } finally {
                if (mutex != null) {
                    mutex.ReleaseMutex();
                }
            }
        }

        static void Uninitialize(string identifier, Mutex mutex, MemoryMappedFile file, long offset) {
            try {
                if (!mutex.WaitOne(LOCK_TIMEOUT)) {
                    throw new InvalidOperationException("Unable to uninitialize inter-process communication");
                }
            } catch (AbandonedMutexException) {
                mutex.Dispose();
                file.Dispose();
                return;
            }
            try {
                Data me;
                if (identifier != null) {
                    using (var accessor = file.CreateViewAccessor(offset, Marshal.SizeOf(typeof(Data)))) {
                        accessor.Read(0, out me);
                        me.InUse = false;
                        accessor.Write(0, ref me);
                    }
                }
            } finally {
                mutex.ReleaseMutex();
                mutex.Dispose();
                file.Dispose();
            }
        }

        /// <summary>
        /// Closes the listener.
        /// </summary>
        public void Dispose() {
            if (!_disposed) {
                _disposed = true;
                Abort();
                _worker.Join(LOCK_TIMEOUT);
            }
        }
    }
}