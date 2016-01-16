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
using System.Reflection;
using System.Security.Permissions;

namespace Python.Runtime {

    //========================================================================
    // Bundles the information required to support an indexer property.
    //========================================================================

    internal class Indexer {

        public MethodBinder GetterBinder;
        public MethodBinder SetterBinder;

        public Indexer() {
            GetterBinder = new MethodBinder();
            SetterBinder = new MethodBinder();
        }


        public bool CanGet {
            get { 
                return GetterBinder.Count > 0; 
            }
        }

        public bool CanSet {
            get { 
                return SetterBinder.Count > 0; 
            }
        }


        public void AddProperty(PropertyInfo pi) {
            MethodInfo getter = pi.GetGetMethod(true);
            MethodInfo setter = pi.GetSetMethod(true);
            if (getter != null) {
                GetterBinder.AddMethod(getter);
            }
            if (setter != null) {
                SetterBinder.AddMethod(setter);
            }
        }

        internal IntPtr GetItem(IntPtr inst, IntPtr args) {
            return GetterBinder.Invoke(inst, args, IntPtr.Zero);
        }


        internal void SetItem(IntPtr inst, IntPtr args) {
            SetterBinder.Invoke(inst, args, IntPtr.Zero);
        }

        internal bool NeedsDefaultArgs(IntPtr args){
            int pynargs = Runtime.PyTuple_Size(args);
            var methods = SetterBinder.GetMethods();
            if(methods.Length == 0)
                return false;

            MethodBase mi = methods[0];
            ParameterInfo[] pi = mi.GetParameters();
            // need to subtract one for the value
            int clrnargs = pi.Length - 1;
            if (pynargs == clrnargs || pynargs > clrnargs)
                return false;

            for (int v = pynargs; v < clrnargs; v++){
                if (pi[v].DefaultValue == DBNull.Value)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// This will return default arguments a new instance of a tuple. The size 
        ///  of the tuple will indicate the number of default arguments. 
        /// </summary>
        /// <param name="args">This is pointing to the tuple args passed in</param>
        /// <returns>a new instance of the tuple containing the default args</returns>
        internal IntPtr GetDefaultArgs(IntPtr args){

            // if we don't need default args return empty tuple
            if(!NeedsDefaultArgs(args))
                return Runtime.PyTuple_New(0);
            int pynargs = Runtime.PyTuple_Size(args);
           
            // Get the default arg tuple
            var methods = SetterBinder.GetMethods();
            MethodBase mi = methods[0];
            ParameterInfo[] pi = mi.GetParameters();
            int clrnargs = pi.Length - 1;
            IntPtr defaultArgs = Runtime.PyTuple_New(clrnargs - pynargs);
            for (int i = 0; i < (clrnargs - pynargs); i++) {
                if (pi[i + pynargs].DefaultValue == DBNull.Value)
                    continue;
                else{
                    IntPtr arg = Converter.ToPython(pi[i + pynargs].DefaultValue, pi[i + pynargs].ParameterType);
                    Runtime.PyTuple_SetItem(defaultArgs, i, arg);
                }
            }
            return defaultArgs;
        }


    }

}
