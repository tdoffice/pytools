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
using System.Linq;
using System.Windows.Threading;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Parsing;
using Microsoft.PythonTools.Parsing.Ast;
using Microsoft.PythonTools.Project;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;

namespace Microsoft.PythonTools.Intellisense {
    sealed class UnresolvedImportSquiggleProvider {
        // Allows test cases to skip checking user options
        internal static bool _alwaysCreateSquiggle;

        private readonly Lazy<TaskProvider> _taskProvider;

        public UnresolvedImportSquiggleProvider(Lazy<TaskProvider> taskProvider) {
            _taskProvider = taskProvider;
        }

        public void ListenForNextNewAnalysis(IPythonProjectEntry entry) {
            if (entry != null && !string.IsNullOrEmpty(entry.FilePath)) {
                entry.OnNewAnalysis += OnNewAnalysis;
            }
        }

        public void StopListening(IPythonProjectEntry entry) {
            if (entry != null) {
                entry.OnNewAnalysis -= OnNewAnalysis;
            }
        }

        private void OnNewAnalysis(object sender, EventArgs e) {
            if (!_alwaysCreateSquiggle &&
                PythonToolsPackage.Instance != null &&
                !PythonToolsPackage.Instance.GeneralOptionsPage.UnresolvedImportWarning
            ) {
                return;
            }

            var entry = sender as IPythonProjectEntry;
            if (entry == null ||
                entry.Analysis == null ||
                entry.Analysis.ProjectState == null ||
                string.IsNullOrEmpty(entry.ModuleName) ||
                string.IsNullOrEmpty(entry.FilePath)
            ) {
                return;
            }

            var analyzer = entry.Analysis.ProjectState;

            PythonAst ast;
            IAnalysisCookie cookie;
            entry.GetTreeAndCookie(out ast, out cookie);
            var snapshotCookie = cookie as SnapshotCookie;
            if (snapshotCookie == null) {
                return;
            }

            var walker = new ImportStatementWalker(entry, analyzer);
            ast.Walk(walker);

            if (walker.Imports.Any()) {
                var f = new TaskProviderItemFactory(snapshotCookie.Snapshot);

                _taskProvider.Value.ReplaceItems(
                    entry,
                    VsProjectAnalyzer.UnresolvedImportMoniker,
                    walker.Imports.Select(t => f.FromUnresolvedImport(
                        analyzer.InterpreterFactory as IPythonInterpreterFactoryWithDatabase,
                        t.Item1,
                        t.Item2.GetSpan(ast)
                    )).ToList()
                );
            }
        }

        class ImportStatementWalker : PythonWalker {
            public readonly List<Tuple<string, DottedName>> Imports = new List<Tuple<string, DottedName>>();

            readonly IPythonProjectEntry _entry;
            readonly PythonAnalyzer _analyzer;

            public ImportStatementWalker(IPythonProjectEntry entry, PythonAnalyzer analyzer) {
                _entry = entry;
                _analyzer = analyzer;
            }

            public override bool Walk(FromImportStatement node) {
                var name = node.Root.MakeString();
                if (!_analyzer.IsModuleResolved(_entry, name, node.ForceAbsolute)) {
                    Imports.Add(Tuple.Create(name, node.Root));
                }
                return base.Walk(node);
            }

            public override bool Walk(ImportStatement node) {
                foreach (var nameNode in node.Names) {
                    var name = nameNode.MakeString();
                    if (!_analyzer.IsModuleResolved(_entry, name, node.ForceAbsolute)) {
                        Imports.Add(Tuple.Create(name, nameNode));
                    }
                }
                return base.Walk(node);
            }
        }
    }
}
