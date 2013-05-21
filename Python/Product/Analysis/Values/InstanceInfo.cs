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

using System.Collections.Generic;
using System.Linq;
using Microsoft.PythonTools.Analysis.Interpreter;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Parsing;
using Microsoft.PythonTools.Parsing.Ast;

namespace Microsoft.PythonTools.Analysis.Values {
    /// <summary>
    /// Represents an instance of a class implemented in Python
    /// </summary>
    internal class InstanceInfo : AnalysisValue, IReferenceableContainer {
        private readonly ClassInfo _classInfo;
        private Dictionary<string, VariableDef> _instanceAttrs;

        public InstanceInfo(ClassInfo classInfo) {
            _classInfo = classInfo;
        }

        public override IDictionary<string, IAnalysisSet> GetAllMembers(IModuleContext moduleContext) {
            var res = new Dictionary<string, IAnalysisSet>();
            if (_instanceAttrs != null) {
                foreach (var kvp in _instanceAttrs) {
                    var types = kvp.Value.TypesNoCopy;
                    var key = kvp.Key;
                    kvp.Value.ClearOldValues();
                    if (kvp.Value.VariableStillExists) {
                        MergeTypes(res, key, types);
                    }
                }
            }

            // check and see if it's defined in a base class instance as well...
            foreach (var b in _classInfo.Bases) {
                foreach (var ns in b) {
                    if (ns.Push()) {
                        try {
                            ClassInfo baseClass = ns as ClassInfo;
                            if (baseClass != null &&
                                baseClass.Instance._instanceAttrs != null) {
                                foreach (var kvp in baseClass.Instance._instanceAttrs) {
                                    kvp.Value.ClearOldValues();
                                    if (kvp.Value.VariableStillExists) {
                                        MergeTypes(res, kvp.Key, kvp.Value.TypesNoCopy);
                                    }
                                }
                            }
                        } finally {
                            ns.Pop();
                        }
                    }
                }
            }

            foreach (var classMem in _classInfo.GetAllMembers(moduleContext)) {
                MergeTypes(res, classMem.Key, classMem.Value);
            }
            return res;
        }

        private static void MergeTypes(Dictionary<string, IAnalysisSet> res, string key, IEnumerable<AnalysisValue> types) {
            IAnalysisSet set;
            if (!res.TryGetValue(key, out set)) {
                res[key] = set = AnalysisSet.Create(types);
            } else {
                res[key] = set.Union(types);
            }
        }

        public Dictionary<string, VariableDef> InstanceAttributes {
            get {
                return _instanceAttrs;
            }
        }

        public PythonAnalyzer ProjectState {
            get {
                return _classInfo.AnalysisUnit.ProjectState;
            }
        }

        public override IAnalysisSet GetMember(Node node, AnalysisUnit unit, string name) {
            // __getattribute__ takes precedence over everything.
            IAnalysisSet getattrRes = AnalysisSet.Empty;
            var getAttribute = _classInfo.GetMemberNoReferences(node, unit.CopyForEval(), "__getattribute__");
            if (getAttribute.Count > 0) {
                foreach (var getAttrFunc in getAttribute) {
                    var func = getAttrFunc as BuiltinMethodInfo;
                    if (func != null && func.Function.DeclaringType.TypeId == BuiltinTypeId.Object) {
                        continue;
                    }
                    // TODO: We should really do a get descriptor / call here
                    getattrRes = getattrRes.Union(getAttrFunc.Call(node, unit, new[] { SelfSet, ProjectState.ClassInfos[BuiltinTypeId.Str].Instance.SelfSet }, ExpressionEvaluator.EmptyNames));
                }
                if (getattrRes.Count > 0) {
                    return getattrRes;
                }
            }
            
            // then check class members
            var classMem = _classInfo.GetMemberNoReferences(node, unit, name);
            if (classMem.Count > 0) {
                var desc = classMem.GetDescriptor(node, this, _classInfo, unit);
                if (desc.Count > 0) {
                    // TODO: Check if it's a data descriptor...
                    return desc;
                }
            } else {
                // if the class gets a value later we need to be re-analyzed
                _classInfo.Scope.CreateEphemeralVariable(node, unit, name, false).AddDependency(unit);
            }
           
            // ok, it most be an instance member...
            if (_instanceAttrs == null) {
                _instanceAttrs = new Dictionary<string, VariableDef>();
            }
            VariableDef def;
            if (!_instanceAttrs.TryGetValue(name, out def)) {
                _instanceAttrs[name] = def = new EphemeralVariableDef();
            }
            def.AddReference(node, unit);
            def.AddDependency(unit);

            // check and see if it's defined in a base class instance as well...
            var res = def.Types;
            foreach (var b in _classInfo.Bases) {
                foreach (var ns in b) {
                    if (ns.Push()) {
                        try {
                            ClassInfo baseClass = ns as ClassInfo;
                            if (baseClass != null &&
                                baseClass.Instance._instanceAttrs != null &&
                                baseClass.Instance._instanceAttrs.TryGetValue(name, out def)) {
                                res = res.Union(def.TypesNoCopy);
                            }
                        } finally {
                            ns.Pop();
                        }
                    }
                }
            }
            
            if (res.Count == 0) {
                // and if that doesn't exist fall back to __getattr__
                var getAttr = _classInfo.GetMemberNoReferences(node, unit, "__getattr__");
                if (getAttr.Count > 0) {
                    foreach (var getAttrFunc in getAttr) {
                        // TODO: We should really do a get descriptor / call here
                        //FIXME: new string[0]
                        getattrRes = getattrRes.Union(getAttrFunc.Call(node, unit, new[] { SelfSet, _classInfo.AnalysisUnit.ProjectState.ClassInfos[BuiltinTypeId.Str].Instance.SelfSet }, ExpressionEvaluator.EmptyNames));
                    }
                }
                return getattrRes;
            }
            return res;
        }

        public override IAnalysisSet GetDescriptor(Node node, AnalysisValue instance, AnalysisValue context, AnalysisUnit unit) {
            var getter = _classInfo.GetMemberNoReferences(node, unit, "__get__");
            if (getter.Count > 0) {
                var get = getter.GetDescriptor(node, this, _classInfo, unit);
                return get.Call(node, unit, new[] { instance, context }, ExpressionEvaluator.EmptyNames);
            }
            return SelfSet;
        }

        public override void SetMember(Node node, AnalysisUnit unit, string name, IAnalysisSet value) {
            if (_instanceAttrs == null) {
                _instanceAttrs = new Dictionary<string, VariableDef>();
            }

            VariableDef instMember;
            if (!_instanceAttrs.TryGetValue(name, out instMember) || instMember == null) {
                _instanceAttrs[name] = instMember = new VariableDef();
            }
            instMember.AddAssignment(node, unit);
            instMember.MakeUnionStrongerIfMoreThan(ProjectState.Limits.InstanceMembers, value);
            instMember.AddTypes(unit, value);
        }

        public override void DeleteMember(Node node, AnalysisUnit unit, string name) {
            if (_instanceAttrs == null) {
                _instanceAttrs = new Dictionary<string, VariableDef>();
            }
            
            VariableDef instMember;
            if (!_instanceAttrs.TryGetValue(name, out instMember) || instMember == null) {
                _instanceAttrs[name] = instMember = new VariableDef();
            }

            instMember.AddReference(node, unit);

            _classInfo.GetMember(node, unit, name);
        }

        public override IAnalysisSet BinaryOperation(Node node, AnalysisUnit unit, PythonOperator operation, IAnalysisSet rhs) {
            string op = null;
            switch (operation) {
                case PythonOperator.Multiply: op = "__mul__"; break;
                case PythonOperator.Add: op = "__add__"; break;
                case PythonOperator.Subtract: op = "__sub__"; break;
                case PythonOperator.Xor: op = "__xor__"; break;
                case PythonOperator.BitwiseAnd: op = "__and__"; break;
                case PythonOperator.BitwiseOr: op = "__or__"; break;
                case PythonOperator.Divide: op = "__div__"; break;
                case PythonOperator.FloorDivide: op = "__floordiv__"; break;
                case PythonOperator.LeftShift: op = "__lshift__"; break;
                case PythonOperator.Mod: op = "__mod__"; break;
                case PythonOperator.Power: op = "__pow__"; break;
                case PythonOperator.RightShift: op = "__rshift__"; break;
                case PythonOperator.TrueDivide: op = "__truediv__"; break;
            }

            if (op != null) {
                var invokeMem = GetMember(node, unit, op);
                if (invokeMem.Count > 0) {
                    // call __*__ method
                    return invokeMem.Call(node, unit, new[] { rhs }, ExpressionEvaluator.EmptyNames);
                }
            }

            return base.BinaryOperation(node, unit, operation, rhs);
        }

        public override IAnalysisSet ReverseBinaryOperation(Node node, AnalysisUnit unit, PythonOperator operation, IAnalysisSet rhs) {
            string op = null;
            switch (operation) {
                case PythonOperator.Multiply: op = "__rmul__"; break;
                case PythonOperator.Add: op = "__radd__"; break;
                case PythonOperator.Subtract: op = "__rsub__"; break;
                case PythonOperator.Xor: op = "__rxor__"; break;
                case PythonOperator.BitwiseAnd: op = "__rand__"; break;
                case PythonOperator.BitwiseOr: op = "__ror__"; break;
                case PythonOperator.Divide: op = "__rdiv__"; break;
                case PythonOperator.FloorDivide: op = "__rfloordiv__"; break;
                case PythonOperator.LeftShift: op = "__rlshift__"; break;
                case PythonOperator.Mod: op = "__rmod__"; break;
                case PythonOperator.Power: op = "__rpow__"; break;
                case PythonOperator.RightShift: op = "__rrshift__"; break;
                case PythonOperator.TrueDivide: op = "__rtruediv__"; break;
            }

            if (op != null) {
                var invokeMem = GetMember(node, unit, op);
                if (invokeMem.Count > 0) {
                    // call __r*__ method
                    return invokeMem.Call(node, unit, new[] { rhs }, ExpressionEvaluator.EmptyNames);
                }
            }

            return base.ReverseBinaryOperation(node, unit, operation, rhs);
        }

        public override IPythonProjectEntry DeclaringModule {
            get {
                return _classInfo.DeclaringModule;
            }
        }

        public override int DeclaringVersion {
            get {
                return _classInfo.DeclaringVersion;
            }
        }

        public override string Description {
            get {
                return ClassInfo.ClassDefinition.Name + " instance";
            }
        }

        public override string Documentation {
            get {
                return ClassInfo.Documentation;
            }
        }

        public override PythonMemberType MemberType {
            get {
                return PythonMemberType.Instance;
            }
        }

        internal override bool IsOfType(IAnalysisSet klass) {
            return klass.Contains(ClassInfo) || klass.Contains(ProjectState.ClassInfos[BuiltinTypeId.Object]);
        }

        public ClassInfo ClassInfo {
            get { return _classInfo; }
        }

        public override string ToString() {
            return ClassInfo.AnalysisUnit.FullName + " instance";
        }

        internal override bool UnionEquals(AnalysisValue ns, int strength) {
            if (strength >= MergeStrength.ToObject) {
                if (ns.TypeId == BuiltinTypeId.NoneType) {
                    // II + BII(None) => do not merge
                    return false;
                }

                // II + II => BII(object)
                // II + BII => BII(object)
                var obj = ProjectState.ClassInfos[BuiltinTypeId.Object];
                return ns is InstanceInfo || 
                    (ns is BuiltinInstanceInfo && ns.TypeId != BuiltinTypeId.Type && ns.TypeId != BuiltinTypeId.Function) ||
                    ns == obj.Instance;

            } else if (strength >= MergeStrength.ToBaseClass) {
                var ii = ns as InstanceInfo;
                if (ii != null) {
                    return ii.ClassInfo.UnionEquals(ClassInfo, strength);
                }
                var bii = ns as BuiltinInstanceInfo;
                if (bii != null) {
                    return bii.ClassInfo.UnionEquals(ClassInfo, strength);
                }
            }

            return base.UnionEquals(ns, strength);
        }

        internal override int UnionHashCode(int strength) {
            if (strength >= MergeStrength.ToObject) {
                return ProjectState.ClassInfos[BuiltinTypeId.Object].Instance.UnionHashCode(strength);

            } else if (strength >= MergeStrength.ToBaseClass) {
                return ClassInfo.UnionHashCode(strength);
            }

            return base.UnionHashCode(strength);
        }

        internal override AnalysisValue UnionMergeTypes(AnalysisValue ns, int strength) {
            if (strength >= MergeStrength.ToObject) {
                // II + II => BII(object)
                // II + BII => BII(object)
                return ProjectState.ClassInfos[BuiltinTypeId.Object].Instance;

            } else if (strength >= MergeStrength.ToBaseClass) {
                var ii = ns as InstanceInfo;
                if (ii != null) {
                    return ii.ClassInfo.UnionMergeTypes(ClassInfo, strength).GetInstanceType().Single();
                }
                var bii = ns as BuiltinInstanceInfo;
                if (bii != null) {
                    return bii.ClassInfo.UnionMergeTypes(ClassInfo, strength).GetInstanceType().Single();
                }
            }

            return base.UnionMergeTypes(ns, strength);
        }

        #region IVariableDefContainer Members

        public IEnumerable<IReferenceable> GetDefinitions(string name) {
            VariableDef def;
            if (_instanceAttrs != null && _instanceAttrs.TryGetValue(name, out def)) {
                yield return def;
            }

            foreach (var classDef in _classInfo.GetDefinitions(name)) {
                yield return classDef;
            }
        }

        #endregion
    }
}
