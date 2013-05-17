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

using Microsoft.PythonTools.Analysis.Interpreter;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Parsing;
using Microsoft.PythonTools.Parsing.Ast;

namespace Microsoft.PythonTools.Analysis.Values {
    class SequenceBuiltinInstanceInfo : BuiltinInstanceInfo {
        private readonly bool _supportsMod;
        private readonly INamespaceSet _indexTypes;
        private INamespaceSet _iterMethod;

        public SequenceBuiltinInstanceInfo(BuiltinClassInfo klass, bool sequenceOfSelf, bool supportsMod)
            : base(klass) {
            _supportsMod = supportsMod;

            var seqInfo = klass as SequenceBuiltinClassInfo;
            if (seqInfo != null) {
                _indexTypes = seqInfo.IndexTypes;
            } else if (sequenceOfSelf) {
                _indexTypes = SelfSet;
            } else {
                _indexTypes = NamespaceSet.Empty;
            }
        }

        public override INamespaceSet GetIndex(Node node, AnalysisUnit unit, INamespaceSet index) {
            return _indexTypes;
        }

        public override INamespaceSet GetEnumeratorTypes(Node node, AnalysisUnit unit) {
            return _indexTypes;
        }

        public override INamespaceSet GetMember(Node node, AnalysisUnit unit, string name) {
            if (name == "__iter__") {
                if (_iterMethod == null) {
                    var indexTypes = new[] { new VariableDef() };
                    indexTypes[0].AddTypes(unit, _indexTypes, false);
                    _iterMethod = new IterBoundBuiltinMethodInfo(indexTypes, ClassInfo);
                }
                return _iterMethod;
            }

            return base.GetMember(node, unit, name);
        }

        public override INamespaceSet BinaryOperation(Node node, AnalysisUnit unit, Parsing.PythonOperator operation, INamespaceSet rhs) {
            var res = NamespaceSet.Empty;
            switch (operation) {
                case PythonOperator.Add:
                    foreach (var type in rhs) {
                        if (type.IsOfType(ClassInfo)) {
                            res = res.Union(ClassInfo.Instance);
                        } else {
                            res = res.Union(type.ReverseBinaryOperation(node, unit, operation, SelfSet));
                        }
                    }
                    break;
                case PythonOperator.Mod:
                    if (_supportsMod) {
                        res = SelfSet;
                    }
                    break;
                case PythonOperator.Multiply:
                    foreach (var type in rhs) {
                        if (type.IsOfType(ProjectState.ClassInfos[BuiltinTypeId.Int]) || type.IsOfType(ProjectState.ClassInfos[BuiltinTypeId.Long])) {
                            res = res.Union(ClassInfo.Instance);
                        } else {
                            var partialRes = ConstantInfo.NumericOp(node, this, unit, operation, rhs);
                            if (partialRes != null) {
                                res = res.Union(partialRes);
                            }
                        }
                    }
                    break;
            }
            return res ?? base.BinaryOperation(node, unit, operation, rhs);
        }

        public override string ToString() {
            return Description;
        }

        public override string Description {
            get {
                if (_indexTypes == this) {
                    return _type.Name;
                } else {
                    return IterableInfo.MakeDescription(this, _type.Name, _indexTypes);
                }
            }
        }

    }
}
