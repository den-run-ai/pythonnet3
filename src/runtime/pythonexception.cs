// ==========================================================================
// This software is subject to the provisions of the Zope Public License,
// Version 2.0 (ZPL).  A copy of the ZPL should accompany this distribution.
// THIS SOFTWARE IS PROVIDED "AS IS" AND ANY AND ALL EXPRESS OR IMPLIED
// WARRANTIES ARE DISCLAIMED, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF TITLE, MERCHANTABILITY, AGAINST INFRINGEMENT, AND FITNESS
// FOR A PARTICULAR PURPOSE.
// ==========================================================================

using System;

namespace Python.Runtime {

    /// <summary>
    /// Provides a managed interface to exceptions thrown by the Python 
    /// runtime.
    /// </summary>

    public class PythonException : System.Exception {

    private IntPtr _pyType = IntPtr.Zero;
    private IntPtr _pyValue = IntPtr.Zero;
    private IntPtr _pyTB = IntPtr.Zero;
    private string _tb = "";
    private string _message = "";
    private bool disposed = false;

    public PythonException() : base()
    {
        IntPtr gs = PythonEngine.AcquireLock();
        Runtime.PyErr_Fetch(ref _pyType, ref _pyValue, ref _pyTB);
        Runtime.Incref(_pyType);
        Runtime.Incref(_pyValue);
        Runtime.Incref(_pyTB);
        if ((_pyType != IntPtr.Zero) && (_pyValue != IntPtr.Zero))
        {
            string type;
            string message;
            Runtime.Incref(_pyType);
            using (PyObject pyType = new PyObject(_pyType))
            using (PyObject pyTypeName = pyType.GetAttr("__name__"))
            {
                    type = pyTypeName.ToString();
            }

            Runtime.Incref(_pyValue);
            using (PyObject pyValue = new PyObject(_pyValue)) 
            {
                message = pyValue.ToString(); 
            }
            _message = type + " : " + message;
        }
        if (_pyTB != IntPtr.Zero)
        {
            PyObject tb_module = PythonEngine.ImportModule("traceback");
            Runtime.Incref(_pyTB);
            using (PyObject pyTB = new PyObject(_pyTB)) {
                _tb = tb_module.InvokeMethod("format_tb", pyTB).ToString();
            }
        }
        PythonEngine.ReleaseLock(gs);
    }

    // Ensure that encapsulated Python objects are decref'ed appropriately
    // when the managed exception wrapper is garbage-collected.

    ~PythonException() {
        Dispose();
    }

    /// <summary>
    /// Restores python error.
    /// </summary>
    public void Restore()
    {
        IntPtr gs = PythonEngine.AcquireLock();
        Runtime.PyErr_Restore(_pyType, _pyValue, _pyTB);
        _pyType = IntPtr.Zero;
        _pyValue = IntPtr.Zero;
        _pyTB = IntPtr.Zero;
        PythonEngine.ReleaseLock(gs);
    }

    /// <summary>
    /// PyType Property
    /// </summary>
    ///
    /// <remarks>
    /// Returns the exception type as a Python object.
    /// </remarks>

    public IntPtr PyType 
    {
        get { return _pyType; }
    }

    /// <summary>
    /// PyValue Property
    /// </summary>
    ///
    /// <remarks>
    /// Returns the exception value as a Python object.
    /// </remarks>

    public IntPtr PyValue
    {
        get { return _pyValue; }
    }

    /// <summary>
    /// PyTB Property
    /// </summary>
    ///
    /// <remarks>
    /// Returns the TraceBack as a Python object.
    /// </remarks>

    public IntPtr PyTB {
        get { return _pyTB; }
    }

    /// <summary>
    /// Message Property
    /// </summary>
    ///
    /// <remarks>
    /// A string representing the python exception message.
    /// </remarks>

    public override string Message
    {
        get { return _message; }
    }

    /// <summary>
    /// StackTrace Property
    /// </summary>
    ///
    /// <remarks>
    /// A string representing the python exception stack trace.
    /// </remarks>

    public override string StackTrace 
    {
        get { return _tb; }
    }


    /// <summary>
    /// Dispose Method
    /// </summary>
    ///
    /// <remarks>
    /// The Dispose method provides a way to explicitly release the 
    /// Python objects represented by a PythonException.
    /// </remarks>

    public void Dispose() {
        if (!disposed) {
        if (Runtime.Py_IsInitialized() > 0) {
            IntPtr gs = PythonEngine.AcquireLock();
            Runtime.Decref(_pyType);
            Runtime.Decref(_pyValue);
            // XXX Do we ever get TraceBack? //
            if (_pyTB != IntPtr.Zero) {
                Runtime.Decref(_pyTB);
            }
            PythonEngine.ReleaseLock(gs);
        }
        GC.SuppressFinalize(this);
        disposed = true;
        }
    }

    /// <summary>
    /// Matches Method
    /// </summary>
    ///
    /// <remarks>
    /// Returns true if the Python exception type represented by the 
    /// PythonException instance matches the given exception type.
    /// </remarks>

    public static bool Matches(IntPtr ob) {
        return Runtime.PyErr_ExceptionMatches(ob) != 0;
    }

    } 
}
