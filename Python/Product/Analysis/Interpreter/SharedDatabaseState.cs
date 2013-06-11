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
using System.IO;
using System.Linq;
using Microsoft.PythonTools.Interpreter.Default;

namespace Microsoft.PythonTools.Interpreter {
    /// <summary>
    /// Cached state that's shared between multiple PythonTypeDatabase instances.
    /// </summary>
    class SharedDatabaseState : ITypeDatabaseReader {
        private readonly Dictionary<string, IPythonModule> _modules = new Dictionary<string, IPythonModule>();
        private readonly List<Action> _fixups = new List<Action>();
        private List<Action<IPythonType>> _objectTypeFixups = new List<Action<IPythonType>>();
        private readonly string _dbDir;
        private readonly Dictionary<IPythonType, CPythonConstant> _constants = new Dictionary<IPythonType, CPythonConstant>();
        private readonly Dictionary<string, IPythonType> _sequenceTypes = new Dictionary<string, IPythonType>();
        private readonly bool _isDefaultDb;
        private readonly Version _langVersion;
        private readonly List<WeakReference> _corruptListeners = new List<WeakReference>();
        private IBuiltinPythonModule _builtinModule;
        private IPythonType _objectType;
        internal readonly SharedDatabaseState _inner;

        internal const string BuiltinName2x = "__builtin__";
        internal const string BuiltinName3x = "builtins";
        private readonly string _builtinName;

        public SharedDatabaseState(string databaseDirectory,
                                   Version languageVersion,
                                   IBuiltinPythonModule builtinsModule = null)
            : this(databaseDirectory, languageVersion, false, builtinsModule) {
        }

        internal SharedDatabaseState(string databaseDirectory,
            Version languageVersion,
            bool defaultDatabase,
            IBuiltinPythonModule builtinsModule = null) {

            _dbDir = databaseDirectory;
            _langVersion = languageVersion;
            _isDefaultDb = defaultDatabase;
            _builtinName = (_langVersion.Major == 3) ? BuiltinName3x : BuiltinName2x;
            _modules[_builtinName] = _builtinModule = builtinsModule ?? MakeBuiltinModule(databaseDirectory);
            if (_isDefaultDb && _langVersion.Major == 3) {
                _modules[BuiltinName2x] = _builtinModule;
            }

            LoadDatabase(databaseDirectory);
        }

        internal SharedDatabaseState(SharedDatabaseState inner, string databaseDirectory = null) {
            _inner = inner;
            _dbDir = databaseDirectory ?? _inner._dbDir;
            _langVersion = _inner._langVersion;
            _builtinName = _inner._builtinName;

            if (!string.IsNullOrEmpty(databaseDirectory)) {
                LoadDatabase(databaseDirectory);
            }
        }

        /// <summary>
        /// Gets the Python language version associated with this database.
        /// </summary>
        public Version LanguageVersion {
            get { return _langVersion; }
        }

        internal void LoadDatabase(string databaseDirectory) {
            foreach (var file in Directory.GetFiles(databaseDirectory)) {
                if (!file.EndsWith(".idb", StringComparison.OrdinalIgnoreCase) || file.IndexOf('$') != -1) {
                    continue;
                } else if (String.Equals(Path.GetFileNameWithoutExtension(file), _builtinName, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                } else if (_isDefaultDb && String.Equals(Path.GetFileNameWithoutExtension(file), BuiltinName2x, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                string modName = Path.GetFileNameWithoutExtension(file);
                if (_isDefaultDb && _langVersion.Major == 3) {
                    // aliases for 3.x when using the default completion DB
                    switch (modName) {
                        case "cPickle": modName = "_pickle"; break;
                        case "thread": modName = "_thread"; break;
                    }
                }
                _modules[modName] = new CPythonModule(this, modName, file, false);
            }
        }

        private CPythonBuiltinModule MakeBuiltinModule(string databaseDirectory) {
            string filename = Path.Combine(databaseDirectory, _builtinName + ".idb");
            if (_langVersion.Major == 3 && !File.Exists(filename)) {
                // Python 3.x the module is builtins, but we may have __builtin__.idb if
                // we're using the default completion DB that we install w/ PTVS.
                filename = Path.Combine(databaseDirectory, "__builtin__.idb");
            }

            return new CPythonBuiltinModule(this, _builtinName, filename, true);
        }

        public IPythonModule GetModule(string name) {
            IPythonModule res;
            if (_modules.TryGetValue(name, out res)) {
                return res;
            }
            
            if (_isDefaultDb && _langVersion.Major == 3) {
                // aliases for 3.x when using the default completion DB
                switch (name) {
                    case "cPickle": return GetModule("_pickle");
                    case "thread": return GetModule("_thread");
                }
            }

            if (name == BuiltinName2x || name == BuiltinName3x) {
                // Handle both names for builtins if the correct one was not
                // found above.
                var mod = BuiltinModule;
                if (mod != null) {
                    return mod;
                }
            }

            if (_inner != null && (res = _inner.GetModule(name)) != null) {
                return res;
            }

            return null;
        }

        private void AddObjectTypeFixup(Action<IPythonType> assign) {
            var obj = ObjectType;
            if (obj != null) {
                assign(obj);
            } else if (_objectTypeFixups != null) {
                _objectTypeFixups.Add(assign);
            } else {
                throw new InvalidOperationException("Cannot find builtin type 'object'");
            }
        }

        private IPythonType ObjectType {
            get {
                if (_objectType == null) {
                    _objectType = BuiltinModule.GetAnyMember(GetBuiltinTypeName(BuiltinTypeId.Object)) as IPythonType;
                    if (_objectType != null) {
                        var fixups = _objectTypeFixups;
                        _objectTypeFixups = null;
                        foreach (var assign in fixups) {
                            assign(_objectType);
                        }
                    }
                }
                return _objectType;
            }
        }

        /// <summary>
        /// Looks up a type and queues a fixup if the type is not yet available.
        /// Receives a delegate which assigns the value to the appropriate field.
        /// </summary>
        public void LookupType(object typeRefOrList, Action<IPythonType> assign) {
            var typeRef = typeRefOrList as Dictionary<string, object>;
            if (typeRef != null) {
                object value;
                string modName = null, typeName = null;
                List<object> indexTypes = null;
                IPythonType res = null;

                if (typeRef.TryGetValue("module_name", out value)) {
                    modName = value as string;
                }
                if (typeRef.TryGetValue("type_name", out value)) {
                    typeName = value as string;
                }
                if (typeRef.TryGetValue("index_types", out value)) {
                    indexTypes = value as List<object>;
                }

                if (typeName == null) {
                    Debug.Assert(modName == null, "moduleref should not be passed to LookupType");
                    AddObjectTypeFixup(assign);
                    return;
                } else {
                    IPythonModule module;
                    if (modName == null) {
                        res = BuiltinModule.GetAnyMember(typeName) as IPythonType;
                        if (res != null) {
                            assign(res);
                        } else {
                            AddObjectTypeFixup(assign);
                        }
                    } else {
                        module = GetModule(modName);
                        if (module == null) {
                            AddFixup(() => {
                                // Fixup 1: Module was not found.
                                var mod2 = GetModule(modName);
                                if (mod2 != null) {
                                    AssignMemberFromModule(mod2, typeName, indexTypes, assign, true);
                                }
                            });
                            return;
                        }
                        AssignMemberFromModule(module, typeName, indexTypes, assign, true);
                    }
                }
                return;
            }

            var multiple = typeRefOrList as List<object>;
            if (multiple != null) {
                foreach (var typeInfo in multiple) {
                    LookupType(typeInfo, assign);
                }
            }
        }

        private void AssignMemberFromModule(IPythonModule module, string typeName, List<object> indexTypes, Action<IPythonType> assign, bool addFixups) {
            IPythonType res;
            IBuiltinPythonModule builtin;
            if ((builtin = module as IBuiltinPythonModule) != null) {
                res = builtin.GetAnyMember(typeName) as IPythonType;
            } else {
                res = module.GetMember(null, typeName) as IPythonType;
            }
            if (indexTypes != null && res != null) {
                res = new CPythonSequenceType(res, this, indexTypes);
            }
            if (res == null) {
                if (addFixups) {
                    AddFixup(() => {
                        // Fixup 2: Type was not found in module, and we're on our second attempt
                        AssignMemberFromModule(module, typeName, indexTypes, assign, false);
                    });
                    return;
                } else {
                    // TODO: Maybe skip this to reduce noise in loaded database
                    AddObjectTypeFixup(assign);
                }
            } else {
                assign(res);
            }
        }

        public string GetBuiltinTypeName(BuiltinTypeId id) {
            return GetBuiltinTypeName(id, _langVersion);
        }

        public static string GetBuiltinTypeName(BuiltinTypeId id, Version languageVersion) {
            string name;
            switch (id) {
                case BuiltinTypeId.Bool: name = "bool"; break;
                case BuiltinTypeId.Complex: name = "complex"; break;
                case BuiltinTypeId.Dict: name = "dict"; break;
                case BuiltinTypeId.Float: name = "float"; break;
                case BuiltinTypeId.Int: name = "int"; break;
                case BuiltinTypeId.List: name = "list"; break;
                case BuiltinTypeId.Long: name = "long"; break;
                case BuiltinTypeId.Object: name = "object"; break;
                case BuiltinTypeId.Set: name = "set"; break;
                case BuiltinTypeId.Str: name = "str"; break;
                case BuiltinTypeId.Unicode: name = languageVersion.Major == 3 ? "str" : "unicode"; break;
                case BuiltinTypeId.Bytes: name = languageVersion.Major == 3 ? "bytes" : "str"; break;
                case BuiltinTypeId.Tuple: name = "tuple"; break;
                case BuiltinTypeId.Type: name = "type"; break;

                case BuiltinTypeId.BuiltinFunction: name = "builtin_function"; break;
                case BuiltinTypeId.BuiltinMethodDescriptor: name = "builtin_method_descriptor"; break;
                case BuiltinTypeId.DictKeys: name = "dict_keys"; break;
                case BuiltinTypeId.DictValues: name = "dict_values"; break;
                case BuiltinTypeId.DictItems: name = "dict_items"; break;
                case BuiltinTypeId.Function: name = "function"; break;
                case BuiltinTypeId.Generator: name = "generator"; break;
                case BuiltinTypeId.NoneType: name = "NoneType"; break;
                case BuiltinTypeId.Ellipsis: name = "ellipsis"; break;
                case BuiltinTypeId.Module: name = "module_type"; break;
                case BuiltinTypeId.ListIterator: name = "list_iterator"; break;
                case BuiltinTypeId.TupleIterator: name = "tuple_iterator"; break;
                case BuiltinTypeId.SetIterator: name = "set_iterator"; break;
                case BuiltinTypeId.StrIterator: name = "str_iterator"; break;
                case BuiltinTypeId.UnicodeIterator: name = "str_iterator"; break;
                case BuiltinTypeId.BytesIterator: name = languageVersion.Major == 3 ? "bytes_iterator" : "str_iterator"; break;
                case BuiltinTypeId.CallableIterator: name = "callable_iterator"; break;

                case BuiltinTypeId.Property: name = "property"; break;
                case BuiltinTypeId.ClassMethod: name = "classmethod"; break;
                case BuiltinTypeId.StaticMethod: name = "staticmethod"; break;
                case BuiltinTypeId.FrozenSet: name = "frozenset"; break;

                case BuiltinTypeId.Unknown:
                default:
                    return null;
            }
            return name;
        }

        /// <summary>
        /// Adds a custom action which will attempt to resolve a type lookup which failed because the
        /// type was not yet defined.  All fixups are run after the database is loaded so all types
        /// should be available.
        /// </summary>
        private void AddFixup(Action action) {
            _fixups.Add(action);
        }

        /// <summary>
        /// Runs all of the custom fixup actions.
        /// </summary>
        public void RunFixups() {
            // we don't use foreach here because we can add fixups while
            // running fixups, in which case we want to keep processing
            // the additional fixups.
            for (int i = 0; i < _fixups.Count; i++) {
                _fixups[i]();
            }

            _fixups.Clear();
        }

        public void ReadMember(string memberName, Dictionary<string, object> memberValue, Action<string, IMember> assign, IMemberContainer container) {
            object memberKind;
            object value;
            Dictionary<string, object> valueDict;

            if (memberValue.TryGetValue("value", out value) &&
                (valueDict = (value as Dictionary<string, object>)) != null &&
                memberValue.TryGetValue("kind", out memberKind) && memberKind is string) {
                switch ((string)memberKind) {
                    case "function":
                        if (CheckVersion(valueDict)) {
                            assign(memberName, new CPythonFunction(this, memberName, valueDict, container));
                        }
                        break;
                    case "func_ref":
                        string funcName;
                        if (valueDict.TryGetValue("func_name", out value) && (funcName = value as string) != null) {
                            var names = funcName.Split('.');
                            IPythonModule mod = GetModule(names[0]);
                            if (mod != null) {
                                if (names.Length == 2) {
                                    var mem = mod.GetMember(null, names[1]);
                                    if (mem == null) {
                                        AddFixup(() => {
                                            var tmp = mod.GetMember(null, names[1]);
                                            if (tmp != null) {
                                                assign(memberName, tmp);
                                            }
                                        });
                                    } else {
                                        assign(memberName, mem);
                                    }
                                } else {
                                    LookupType(new object[] { names[0], names[1] }, type => {
                                        var mem = type.GetMember(null, names[2]);
                                        if (mem != null) {
                                            assign(memberName, mem);
                                        }
                                    });
                                }
                            }
                        }
                        break;
                    case "method":
                        if (CheckVersion(valueDict)) {
                            assign(memberName, new CPythonMethodDescriptor(this, memberName, valueDict, container));
                        }
                        break;
                    case "property":
                        if (CheckVersion(valueDict)) {
                            assign(memberName, new CPythonProperty(this, valueDict, container));
                        }
                        break;
                    case "data":
                        object typeInfo;
                        if (valueDict.TryGetValue("type", out typeInfo) && CheckVersion(valueDict)) {
                            LookupType(
                                typeInfo,
                                dataType => {
                                    if (!(dataType is IPythonSequenceType)) {
                                        assign(memberName, GetConstant(dataType));
                                    } else {
                                        assign(memberName, dataType);
                                    }
                                }
                            );
                        }
                        break;
                    case "type":
                        if (CheckVersion(valueDict)) {
                            assign(memberName, MakeType(memberName, valueDict, container));
                        }
                        break;
                    case "multiple":
                        object members;
                        object[] memsArray;
                        if (valueDict.TryGetValue("members", out members) && (memsArray = members as object[]) != null) {
                            IMember[] finalMembers = GetMultipleMembers(memberName, container, memsArray);
                            assign(memberName, new CPythonMultipleMembers(finalMembers));
                        }
                        break;
                    case "typeref":
                        LookupType(valueDict, dataType => assign(memberName, dataType));
                        break;
                    case "moduleref":
                        object modName;
                        if (!valueDict.TryGetValue("module_name", out modName) || !(modName is string)) {
                            throw new InvalidOperationException("Failed to find module name: " + modName);
                        }

                        assign(memberName, GetModule((string)modName));
                        break;
                }
            }
        }

        /// <summary>
        /// Raises the notification that the database is corrupt, called when reading
        /// a module definition fails.
        /// </summary>
        public void OnDatabaseCorrupt() {
            WeakReference[] listeners;
            lock (_corruptListeners) {
                listeners = _corruptListeners.ToArray();
            }
            for (int i = 0; i < listeners.Length; i++) {
                var target = listeners[i].Target;
                if (target != null) {
                    ((PythonTypeDatabase)target).OnDatabaseCorrupt();
                }
            }
        }

        /// <summary>
        /// Sets up a weak reference for notification of when the shared database
        /// has become corrupted.  Doesn't keep the listening database alive.
        /// </summary>
        public void ListenForCorruptDatabase(PythonTypeDatabase db) {
            lock (_corruptListeners) {
                for (int i = 0; i < _corruptListeners.Count; i++) {
                    var target = _corruptListeners[i].Target;
                    if (target == null) {
                        _corruptListeners[i].Target = db;
                        return;
                    }
                }

                _corruptListeners.Add(new WeakReference(db));
            }
        }

        private bool CheckVersion(Dictionary<string, object> valueDict) {
            object version;
            return !valueDict.TryGetValue("version", out version) || VersionApplies(version);
        }

        /// <summary>
        /// Checks to see if this member is applicable to our current language version for the shared DB.
        /// 
        /// Version formats are specified in the format:
        /// 
        /// version_check|version_checks
        /// 
        /// version_check:
        ///     greater_equals_check
        ///     less_equals_check
        ///     equals_check
        ///     
        /// greater_equals_check:   &gt;=version
        /// less_equals_check:      &lt;=version
        /// equals_check            ==version
        /// 
        /// version:    major_version.minor_version
        /// major_version: number
        /// minor_version: number
        /// 
        /// version_checks:  version_check(;version_check)+
        /// 
        /// For the member to be included all checks must pass.
        /// </summary>
        internal bool VersionApplies(object version) {
            if (_langVersion == null || version == null) {
                return true;
            }

            string strVer = version as string;
            if (strVer != null) {
                if (strVer.IndexOf(';') != -1) {
                    foreach (var curVersion in strVer.Split(';')) {
                        if (!OneVersionApplies(curVersion)) {
                            return false;
                        }
                    }
                    return true;
                } else {
                    return OneVersionApplies(strVer);
                }
            }
            return false;
        }

        private bool OneVersionApplies(string strVer) {
            Version specifiedVer;
            if (strVer.StartsWith(">=")) {
                if (Version.TryParse(strVer.Substring(2), out specifiedVer) && _langVersion >= specifiedVer) {
                    return true;
                }
            } else if (strVer.StartsWith("<=")) {
                if (Version.TryParse(strVer.Substring(2), out specifiedVer) && _langVersion <= specifiedVer) {
                    return true;
                }
            } else if (strVer.StartsWith("==")) {
                if (Version.TryParse(strVer.Substring(2), out specifiedVer) && _langVersion == specifiedVer) {
                    return true;
                }
            }
            return false;
        }

        private IMember[] GetMultipleMembers(string memberName, IMemberContainer container, object[] memsArray) {
            IMember[] finalMembers = new IMember[memsArray.Length];
            for (int i = 0; i < finalMembers.Length; i++) {
                var curMember = memsArray[i] as Dictionary<string, object>;
                var tmp = i;    // close over the current value of i, not the last one...
                if (curMember != null) {
                    ReadMember(memberName, curMember, (name, newMemberValue) => finalMembers[tmp] = newMemberValue, container);
                }
            }
            return finalMembers;
        }

        private CPythonType MakeType(string typeName, Dictionary<string, object> valueDict, IMemberContainer container) {
            BuiltinTypeId typeId = BuiltinTypeId.Unknown;
            if (container is IBuiltinPythonModule) {
                typeId = GetBuiltinTypeId(typeName);
            }

            return new CPythonType(container, this, typeName, valueDict, typeId);
        }

        private BuiltinTypeId GetBuiltinTypeId(string typeName) {
            // Never return BuiltinTypeId.Str, StrIterator, or any value where
            // IsVirtualId() is true from this function.
            switch (typeName) {
                case "list": return BuiltinTypeId.List;
                case "tuple": return BuiltinTypeId.Tuple;
                case "float": return BuiltinTypeId.Float;
                case "int": return BuiltinTypeId.Int;
                case "complex": return BuiltinTypeId.Complex;
                case "dict": return BuiltinTypeId.Dict;
                case "bool": return BuiltinTypeId.Bool;
                case "generator": return BuiltinTypeId.Generator;
                case "ModuleType": return BuiltinTypeId.Module;
                case "function": return BuiltinTypeId.Function;
                case "set": return BuiltinTypeId.Set;
                case "type": return BuiltinTypeId.Type;
                case "object": return BuiltinTypeId.Object;
                case "long": return BuiltinTypeId.Long;
                case "str": return _langVersion.Major == 3 ? BuiltinTypeId.Unicode : BuiltinTypeId.Bytes;
                case "unicode": return BuiltinTypeId.Unicode;
                case "bytes": return BuiltinTypeId.Bytes;
                case "builtin_function": return BuiltinTypeId.BuiltinFunction;
                case "builtin_method_descriptor": return BuiltinTypeId.BuiltinMethodDescriptor;
                case "NoneType": return BuiltinTypeId.NoneType;
                case "ellipsis": return BuiltinTypeId.Ellipsis;
                case "dict_keys": return BuiltinTypeId.DictKeys;
                case "dict_values": return BuiltinTypeId.DictValues;
                case "dict_items": return BuiltinTypeId.DictItems;
                case "list_iterator": return BuiltinTypeId.ListIterator;
                case "tuple_iterator": return BuiltinTypeId.TupleIterator;
                case "set_iterator": return BuiltinTypeId.SetIterator;
                case "str_iterator": return BuiltinTypeId.UnicodeIterator;
                case "bytes_iterator": return BuiltinTypeId.BytesIterator;
                case "callable_iterator": return BuiltinTypeId.CallableIterator;
                case "property": return BuiltinTypeId.Property;
                case "classmethod": return BuiltinTypeId.ClassMethod;
                case "staticmethod": return BuiltinTypeId.StaticMethod;
                case "frozenset": return BuiltinTypeId.FrozenSet;
            }
            return BuiltinTypeId.Unknown;
        }

        internal CPythonConstant GetConstant(IPythonType type) {
            CPythonConstant constant;
            for(var state = this; state != null; state = state._inner) {
                if (state._constants.TryGetValue(type, out constant)) {
                    return constant;
                }
            }
            _constants[type] = constant = new CPythonConstant(type);
            return constant;
        }

        public IBuiltinPythonModule BuiltinModule {
            get {
                for(var state = this; state != null; state = state._inner) {
                    if (state._builtinModule != null) {
                        return state._builtinModule;
                    }
                }
                return null;
            }
            set {
                Modules[value.Name] = value;
                _builtinModule = value;
            }
        }

        public Dictionary<string, IPythonModule> Modules {
            get {
                return _modules;
            }
        }

        public string DatabaseDirectory {
            get {
                return _dbDir;
            }
        }
    }
}
