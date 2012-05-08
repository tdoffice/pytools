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
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.PythonTools.Django.TemplateParsing;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.PythonTools.Django.Intellisense {
    [Export(typeof(IIntellisenseControllerProvider)), ContentType(TemplateContentType.ContentTypeName), Order]
    class DjangoIntellisenseControllerProvider : IIntellisenseControllerProvider {
        internal readonly IVsEditorAdaptersFactoryService _adaptersFactory;
        internal readonly ICompletionBroker _broker;

        [ImportingConstructor]
        public DjangoIntellisenseControllerProvider(IVsEditorAdaptersFactoryService adaptersFactory, ICompletionBroker broker) {
            _adaptersFactory = adaptersFactory;
            _broker = broker;
        }

        #region IIntellisenseControllerProvider Members

        public IIntellisenseController TryCreateIntellisenseController(ITextView textView, IList<ITextBuffer> subjectBuffers) {
            DjangoIntellisenseController controller;
            if (!textView.Properties.TryGetProperty<DjangoIntellisenseController>(typeof(DjangoIntellisenseController), out controller)) {
                controller = new DjangoIntellisenseController(this, textView);
                textView.Properties.AddProperty(typeof(DjangoIntellisenseController), controller);
                foreach (var buffer in subjectBuffers) {
                    controller.ConnectSubjectBuffer(buffer);
                }
            }

            return controller;
        }

        #endregion

        internal static DjangoIntellisenseController GetOrCreateController(IComponentModel model, ITextView textView) {
            DjangoIntellisenseController controller;
            if (!textView.Properties.TryGetProperty<DjangoIntellisenseController>(typeof(DjangoIntellisenseController), out controller)) {
                var intellisenseControllerProvider = (
                   from export in model.DefaultExportProvider.GetExports<IIntellisenseControllerProvider, IContentTypeMetadata>()
                   from exportedContentType in export.Metadata.ContentTypes
                   where exportedContentType == TemplateContentType.ContentTypeName && export.Value.GetType() == typeof(DjangoIntellisenseControllerProvider)
                   select export.Value
                ).First();
                controller = new DjangoIntellisenseController((DjangoIntellisenseControllerProvider)intellisenseControllerProvider, textView);
            }
            return controller;
        }

    }

    /// <summary>
    /// Monitors creation of text view adapters for Python code so that we can attach
    /// our keyboard filter.  This enables not using a keyboard pre-preprocessor
    /// so we can process all keys for text views which we attach to.  We cannot attach
    /// our command filter on the text view when our intellisense controller is created
    /// because the adapter does not exist.
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType(TemplateContentType.ContentTypeName)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    class TextViewCreationListener : IVsTextViewCreationListener {
        internal readonly IVsEditorAdaptersFactoryService _adaptersFactory;

        [ImportingConstructor]
        public TextViewCreationListener(IVsEditorAdaptersFactoryService adaptersFactory) {
            _adaptersFactory = adaptersFactory;
        }

        #region IVsTextViewCreationListener Members

        public void VsTextViewCreated(VisualStudio.TextManager.Interop.IVsTextView textViewAdapter) {
            var textView = _adaptersFactory.GetWpfTextView(textViewAdapter);
            DjangoIntellisenseController controller;
            if (textView.Properties.TryGetProperty<DjangoIntellisenseController>(typeof(DjangoIntellisenseController), out controller)) {
                controller.AttachKeyboardFilter();
            }
        }

        #endregion
    }

}
