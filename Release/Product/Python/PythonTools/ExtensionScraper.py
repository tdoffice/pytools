 # ############################################################################
 #
 # Copyright (c) Microsoft Corporation. 
 #
 # This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 # copy of the license can be found in the License.html file at the root of this distribution. If 
 # you cannot locate the Apache License, Version 2.0, please send an email to 
 # vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 # by the terms of the Apache License, Version 2.0.
 #
 # You must not remove this notice, or any other, from this software.
 #
 # ###########################################################################

import sys

try:
    # disable error reporting in our process, bad extension modules can crash us, and we don't
    # want a bunch of Watson boxes popping up...
    import ctypes 
    ctypes.windll.kernel32.SetErrorMode(3)  # SEM_FAILCRITICALERRORS /  SEM_NOGPFAULTERRORBOX
except:
    pass

# Scrapes the file and saves the analysis to the specified filename, exits w/ nonzero exit code if anything goes wrong.
# Usage: ExtensionScraper.py scrape [mod_name or '-'] [mod_path or '-'] [output_path]

if len(sys.argv) != 5 or sys.argv[1].lower() != 'scrape':
    raise ValueError('Expects "ExtensionScraper.py scrape [mod_name|'-'] [mod_path|'-'] [output_path]"')

mod_name, mod_path, output_path = sys.argv[2:]
module = None

if mod_name and mod_name != '-':
    try:
        __import__(mod_name)
        module = sys.modules[mod_name]
    finally:
        if not module:
            print('__import__("' + mod_name + '")')
elif mod_path and mod_path != '-':
    try:
        import imp
        import os.path
        mod_name = os.path.splitext(os.path.split(mod_path)[1])[0]
        module = imp.load_dynamic(mod_name, mod_path)
    finally:
        if not module:
            print('imp.load_dynamic("' + mod_name + '", "' + mod_path + '")')
else:
    raise ValueError('No module name or path provided')

import PythonScraper
analysis = PythonScraper.generate_module(module)
PythonScraper.write_analysis(output_path, analysis)
