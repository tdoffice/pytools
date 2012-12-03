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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PythonTools.Analysis;

namespace Microsoft.PythonTools.Intellisense {
    /// <summary>
    /// Provides a single threaded analysis queue.  Items can be enqueued into the
    /// analysis at various priorities.  
    /// </summary>
    sealed class AnalysisQueue : IDisposable {
        private readonly Thread _workThread;
        private readonly AutoResetEvent _workEvent;
        private readonly VsProjectAnalyzer _analyzer;
        private readonly object _queueLock = new object();
        private readonly List<IAnalyzable>[] _queue;
        private readonly HashSet<IGroupableAnalysisProject> _enqueuedGroups = new HashSet<IGroupableAnalysisProject>();
        private TaskScheduler _scheduler;
        internal bool _unload;
        private bool _isAnalyzing;
        private int _analysisPending;

        private const int PriorityCount = (int)AnalysisPriority.High + 1;

        internal AnalysisQueue(VsProjectAnalyzer analyzer) {
            _workEvent = new AutoResetEvent(false);
            _analyzer = analyzer;

            _queue = new List<IAnalyzable>[PriorityCount];
            for (int i = 0; i < PriorityCount; i++) {
                _queue[i] = new List<IAnalyzable>();
            }

            _workThread = new Thread(Worker);
            _workThread.Name = "Python Analysis Queue";
            _workThread.Priority = ThreadPriority.BelowNormal;
            _workThread.IsBackground = true;
            
            // start the thread, wait for our synchronization context to be created
            using (AutoResetEvent threadStarted = new AutoResetEvent(false)) {
                _workThread.Start(threadStarted);
                threadStarted.WaitOne();
            }
        }

        public TaskScheduler Scheduler {
            get {
                return _scheduler;
            }
        }

        public void Enqueue(IAnalyzable item, AnalysisPriority priority) {
            int iPri = (int)priority;

            if (iPri < 0 || iPri > _queue.Length) {
                throw new ArgumentException("priority");
            }

            lock (_queueLock) {
                // see if we have the item in the queue anywhere...
                for (int i = 0; i < _queue.Length; i++) {
                    if (_queue[i].Remove(item)) {
                        Interlocked.Decrement(ref _analysisPending);

                        AnalysisPriority oldPri = (AnalysisPriority)i;

                        if (oldPri > priority) {
                            // if it was at a higher priority then our current
                            // priority go ahead and raise the new entry to our
                            // old priority
                            priority = oldPri;
                        }

                        break;
                    }
                }

                // enqueue the work item
                Interlocked.Increment(ref _analysisPending);
                if (priority == AnalysisPriority.High) {
                    // always try and process high pri items immediately
                    _queue[iPri].Insert(0, item);
                } else {
                    _queue[iPri].Add(item);
                }
                _workEvent.Set();
            }
        }

        public void Stop() {
            if (_workThread != null) {
                _unload = true;
                _workEvent.Set();
                _workThread.Join();
            }
        }

        public bool IsAnalyzing {
            get {
                return _isAnalyzing;
            }
        }

        public int AnalysisPending {
            get {
                return _analysisPending;
            }
        }

        #region IDisposable Members

        void IDisposable.Dispose() {
            Stop();
        }

        #endregion

        private IAnalyzable GetNextItem(out AnalysisPriority priority) {
            for (int i = PriorityCount - 1; i >= 0; i--) {
                if (_queue[i].Count > 0) {
                    var res = _queue[i][0];
                    _queue[i].RemoveAt(0);
                    Interlocked.Decrement(ref _analysisPending);
                    priority = (AnalysisPriority)i;
                    return res;
                }
            }
            priority = AnalysisPriority.None;
            return null;
        }

        private void Worker(object threadStarted) {
            try {
                SynchronizationContext.SetSynchronizationContext(new AnalysisSynchronizationContext(this));
                _scheduler = TaskScheduler.FromCurrentSynchronizationContext();
            } finally {
                ((AutoResetEvent)threadStarted).Set();
            }

            while (!_unload) {
                IAnalyzable workItem;

                AnalysisPriority pri;
                lock (_queueLock) {
                    workItem = GetNextItem(out pri);
                }
                _isAnalyzing = true;
                if (workItem != null) {
                    var groupable = workItem as IGroupableAnalysisProjectEntry;
                    if (groupable != null) {
                        bool added = _enqueuedGroups.Add(groupable.AnalysisGroup);
                        if (added) {
                            Enqueue(new GroupAnalysis(groupable.AnalysisGroup, this), pri);
                        }

                        groupable.Analyze(true);
                    } else {
                        workItem.Analyze();
                    }
                    _isAnalyzing = false;
                } else {
                    _isAnalyzing = false;
                    WaitHandle.SignalAndWait(
                        _analyzer.QueueActivityEvent,
                        _workEvent
                    );
                }   
            }
        }

        sealed class GroupAnalysis : IAnalyzable {
            private readonly IGroupableAnalysisProject _project;
            private readonly AnalysisQueue _queue;

            public GroupAnalysis(IGroupableAnalysisProject project, AnalysisQueue queue) {
                _project = project;
                _queue = queue;
            }

            #region IAnalyzable Members

            public void Analyze() {
                _queue._enqueuedGroups.Remove(_project);
                _project.AnalyzeQueuedEntries();
            }

            #endregion
        }
    }
}
