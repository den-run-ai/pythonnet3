// ==========================================================================
// This software is subject to the provisions of the Zope Public License,
// Version 2.0 (ZPL).  A copy of the ZPL should accompany this distribution.
// THIS SOFTWARE IS PROVIDED "AS IS" AND ANY AND ALL EXPRESS OR IMPLIED
// WARRANTIES ARE DISCLAIMED, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF TITLE, MERCHANTABILITY, AGAINST INFRINGEMENT, AND FITNESS
// FOR A PARTICULAR PURPOSE.
// ==========================================================================

using System;
using System.Collections;
using System.Runtime.InteropServices;

namespace Python.Runtime {

    /// <summary>
    /// A MethodWrapper wraps a static method of a managed type,
    /// making it callable by Python as a PyCFunction object. This is
    /// currently used mainly to implement special cases like the CLR
    /// import hook.
    /// </summary>

    internal class MethodWrapper {

        public IntPtr mdef;
        public IntPtr ptr;

        public MethodWrapper(Type type, string name) {

            // Turn the managed method into a function pointer

            IntPtr fp = Interop.GetThunk(type.GetMethod(name));

            // XXX - here we create a Python string object, then take the
            // char * of the internal string to pass to our methoddef
            // structure. Its a hack, and the name is leaked!
#if (PYTHON32 || PYTHON33 || PYTHON34 || PYTHON35)
            IntPtr ps = Runtime.PyBytes_FromString(name);
            IntPtr sp = Runtime.PyBytes_AS_STRING(ps);
#else
            IntPtr ps = Runtime.PyString_FromString(name);
            IntPtr sp = Runtime.PyString_AS_STRING(ps);
#endif

            // Allocate and initialize a PyMethodDef structure to represent 
            // the managed method, then create a PyCFunction. 

            mdef = Runtime.PyMem_Malloc(4 * IntPtr.Size);
            Marshal.WriteIntPtr(mdef, sp);
            Marshal.WriteIntPtr(mdef, (1 * IntPtr.Size), fp);
            Marshal.WriteIntPtr(mdef, (2 * IntPtr.Size), (IntPtr)0x0003); // METH_VARARGS | METH_KEYWORDS
            Marshal.WriteIntPtr(mdef, (3 * IntPtr.Size), IntPtr.Zero);
            ptr = Runtime.PyCFunction_NewEx(mdef, IntPtr.Zero, IntPtr.Zero);
        }

        public IntPtr Call(IntPtr args, IntPtr kw) {
            return Runtime.PyCFunction_Call(ptr, args, kw);
        }


    }


}

