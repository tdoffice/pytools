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
using System.Diagnostics;
using Microsoft.PythonTools.Analysis;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;

namespace Microsoft.PythonTools.Intellisense {
    /// <summary>
    /// Provides various completion services after the text around the current location has been
    /// processed. The completion services are specific to the current context
    /// </summary>
    public class CompletionAnalysis {
        private readonly string _text;
        protected readonly int _pos;
        private readonly ITrackingSpan _span;
        private readonly ITextBuffer _textBuffer;
        internal const Int64 TooMuchTime = 50;
        protected static Stopwatch _stopwatch = MakeStopWatch();

        internal static CompletionAnalysis EmptyCompletionContext = new CompletionAnalysis(String.Empty, 0, null, null);

        internal CompletionAnalysis(string text, int pos, ITrackingSpan span, ITextBuffer textBuffer) {
            _text = text ?? String.Empty;
            _pos = pos;
            _span = span;
            _textBuffer = textBuffer;
        }

        public ITextBuffer TextBuffer {
            get {
                return _textBuffer;
            }
        }

        public string Text {
            get {
                return _text;
            }
        }

        public ITrackingSpan Span {
            get {
                return _span;
            }
        }

        public virtual CompletionSet GetCompletions(IGlyphService glyphService) {
            return null;
        }

        internal static bool IsKeyword(ClassificationSpan token, string keyword) {
            return token.ClassificationType.Classification == "keyword" && token.Span.GetText() == keyword;
        }

        internal static Completion PythonCompletion(IGlyphService service, MemberResult memberResult) {
            StandardGlyphGroup group = memberResult.MemberType.ToGlyphGroup();
            var icon = new IconDescription(group, StandardGlyphItem.GlyphItemPublic);

            var result = new LazyCompletion(memberResult.Name, () => memberResult.Completion, () => memberResult.Documentation, service.GetGlyph(group, StandardGlyphItem.GlyphItemPublic));
            result.Properties.AddProperty(typeof(IconDescription), icon);
            return result;
        }

        internal static Completion PythonCompletion(IGlyphService service, string name, string tooltip, StandardGlyphGroup group) {
            var icon = new IconDescription(group, StandardGlyphItem.GlyphItemPublic);

            var result = new LazyCompletion(name, () => name, () => tooltip, service.GetGlyph(group, StandardGlyphItem.GlyphItemPublic));
            result.Properties.AddProperty(typeof(IconDescription), icon);
            return result;
        }

        internal ModuleAnalysis GetAnalysisEntry() {
            return ((IPythonProjectEntry)TextBuffer.GetAnalysis()).Analysis;
        }

        private static Stopwatch MakeStopWatch() {
            var res = new Stopwatch();
            res.Start();
            return res;
        }

        public virtual Completion[] GetModules(IGlyphService glyphService, string text, bool includeMembers = false) {
            var analysis = GetAnalysisEntry();
            var path = text.Split('.');
            if (path.Length > 0) {
                // path = path[:-1]
                var newPath = new string[path.Length - 1];
                Array.Copy(path, newPath, path.Length - 1);
                path = newPath;
            }

            MemberResult[] modules = new MemberResult[0];
            if (path.Length == 0) {
                if (analysis != null) {
                    modules = analysis.ProjectState.GetModules(true);
                }

#if REPL
                var repl = Intellisense.GetRepl(_textBuffer);
                if (repl != null) {
                    modules = Intellisense.MergeMembers(modules, repl.GetModules());
                }
#endif
            } else {
                if (analysis != null) {
                    modules = analysis.ProjectState.GetModuleMembers(analysis.InterpreterContext, path, includeMembers);
                }
            }

            var sortedAndFiltered = NormalCompletionAnalysis.FilterCompletions(modules, text, (x, y) => x.StartsWith(y));
            Array.Sort(sortedAndFiltered, NormalCompletionAnalysis.ModuleSort);

            var result = new Completion[sortedAndFiltered.Length];
            for (int i = 0; i < sortedAndFiltered.Length; i++) {
                result[i] = PythonCompletion(glyphService, sortedAndFiltered[i]);
            }
            return result;
        }

        public override string ToString() {
            return String.Format("CompletionContext({0}): {1} @{2}", GetType().Name, Text, _pos);
        }
    }
}
