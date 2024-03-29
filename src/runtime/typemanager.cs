// ==========================================================================
// This software is subject to the provisions of the Zope Public License,
// Version 2.0 (ZPL).  A copy of the ZPL should accompany this distribution.
// THIS SOFTWARE IS PROVIDED "AS IS" AND ANY AND ALL EXPRESS OR IMPLIED
// WARRANTIES ARE DISCLAIMED, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF TITLE, MERCHANTABILITY, AGAINST INFRINGEMENT, AND FITNESS
// FOR A PARTICULAR PURPOSE.
// ==========================================================================

using System;
using System.Runtime.InteropServices;
using System.Reflection.Emit;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Threading;

namespace Python.Runtime {

    //=======================================================================
    // The TypeManager class is responsible for building binary-compatible
    // Python type objects that are implemented in managed code.
    //=======================================================================

    internal class TypeManager {

        static BindingFlags tbFlags;
        static Dictionary<Type, IntPtr> cache;

        static TypeManager() {
            tbFlags = BindingFlags.Public | BindingFlags.Static;
            cache = new Dictionary<Type, IntPtr>(128);
        }


        //====================================================================
        // Given a managed Type derived from ExtensionType, get the handle to
        // a Python type object that delegates its implementation to the Type
        // object. These Python type instances are used to implement internal
        // descriptor and utility types like ModuleObject, PropertyObject, etc.
        //====================================================================

        internal static IntPtr GetTypeHandle(Type type) {
            // Note that these types are cached with a refcount of 1, so they
            // effectively exist until the CPython runtime is finalized.
            IntPtr handle = IntPtr.Zero;
            cache.TryGetValue(type, out handle);
            if (handle != IntPtr.Zero) {
                return handle;
            }
            handle = CreateType(type);
            cache[type] = handle;
            return handle;
        }


        //====================================================================
        // Get the handle of a Python type that reflects the given CLR type.
        // The given ManagedType instance is a managed object that implements
        // the appropriate semantics in Python for the reflected managed type.
        //====================================================================

        internal static IntPtr GetTypeHandle(ManagedType obj, Type type) {
            IntPtr handle = IntPtr.Zero;
            cache.TryGetValue(type, out handle);
            if (handle != IntPtr.Zero) {
                return handle;
            }
            handle = CreateType(obj, type);
            cache[type] = handle;
            return handle;
        }


        //====================================================================
        // The following CreateType implementations do the necessary work to
        // create Python types to represent managed extension types, reflected
        // types, subclasses of reflected types and the managed metatype. The
        // dance is slightly different for each kind of type due to different
        // behavior needed and the desire to have the existing Python runtime
        // do as much of the allocation and initialization work as possible.
        //====================================================================

        internal static IntPtr CreateType(Type impl) {

            IntPtr type = AllocateTypeObject(impl.Name);
            int ob_size = ObjectOffset.Size(type);

            // Set tp_basicsize to the size of our managed instance objects.
            Marshal.WriteIntPtr(type, TypeOffset.tp_basicsize, (IntPtr)ob_size);

            IntPtr offset = (IntPtr)ObjectOffset.DictOffset(type);
            Marshal.WriteIntPtr(type, TypeOffset.tp_dictoffset, offset);

            InitializeSlots(type, impl);

            int flags = TypeFlags.Default | TypeFlags.Managed |
                        TypeFlags.HeapType | TypeFlags.HaveGC;
            Marshal.WriteIntPtr(type, TypeOffset.tp_flags, (IntPtr)flags);

            Runtime.PyType_Ready(type);

            IntPtr dict = Marshal.ReadIntPtr(type, TypeOffset.tp_dict);
            IntPtr mod = Runtime.PyString_FromString("CLR");
            Runtime.PyDict_SetItemString(dict, "__module__", mod);

            InitMethods(type, impl);

            return type;
        }


        internal static IntPtr CreateType(ManagedType impl, Type clrType) {
            // Cleanup the type name to get rid of funny nested type names.
            string name = "CLR." + clrType.FullName;
            int i = name.LastIndexOf('+');
            if (i > -1) {
                name = name.Substring(i + 1);
            }
            i = name.LastIndexOf('.');
            if (i > -1) {
                name = name.Substring(i + 1);
            }

            IntPtr base_ = IntPtr.Zero;
            int ob_size = ObjectOffset.Size(Runtime.PyTypeType);
            int tp_dictoffset = ObjectOffset.DictOffset(Runtime.PyTypeType);

            // XXX Hack, use a different base class for System.Exception
            // Python 2.5+ allows new style class exceptions but they *must*
            // subclass BaseException (or better Exception).
#if (PYTHON25 || PYTHON26 || PYTHON27 || PYTHON32 || PYTHON33 || PYTHON34 || PYTHON35)
            if (typeof(System.Exception).IsAssignableFrom(clrType))
            {
                ob_size = ObjectOffset.Size(Exceptions.BaseException);
                tp_dictoffset = ObjectOffset.DictOffset(Exceptions.BaseException);
            }

            if (clrType == typeof(System.Exception))
            {
                base_ = Exceptions.Exception;
                Runtime.Incref(base_);
            } else
#endif
            if (clrType.BaseType != null) {
                ClassBase bc = ClassManager.GetClass(clrType.BaseType);
                base_ = bc.pyHandle;
            }

            IntPtr type = AllocateTypeObject(name);

            Marshal.WriteIntPtr(type,TypeOffset.ob_type,Runtime.PyCLRMetaType);
            Runtime.Incref(Runtime.PyCLRMetaType);

            Marshal.WriteIntPtr(type, TypeOffset.tp_basicsize, (IntPtr)ob_size);
            Marshal.WriteIntPtr(type, TypeOffset.tp_itemsize, IntPtr.Zero);
            Marshal.WriteIntPtr(type, TypeOffset.tp_dictoffset, (IntPtr)tp_dictoffset);

            InitializeSlots(type, impl.GetType());

            if (base_ != IntPtr.Zero) {
                Marshal.WriteIntPtr(type, TypeOffset.tp_base, base_);
                Runtime.Incref(base_);
            }

            int flags = TypeFlags.Default;
            flags |= TypeFlags.Managed;
            flags |= TypeFlags.HeapType;
            flags |= TypeFlags.BaseType;
            flags |= TypeFlags.HaveGC;
            Marshal.WriteIntPtr(type, TypeOffset.tp_flags, (IntPtr)flags);

            // Leverage followup initialization from the Python runtime. Note
            // that the type of the new type must PyType_Type at the time we
            // call this, else PyType_Ready will skip some slot initialization.

            Runtime.PyType_Ready(type);

            IntPtr dict = Marshal.ReadIntPtr(type, TypeOffset.tp_dict);
            string mn = clrType.Namespace != null ? clrType.Namespace : "";
            IntPtr mod = Runtime.PyString_FromString(mn);
            Runtime.PyDict_SetItemString(dict, "__module__", mod);

            // Hide the gchandle of the implementation in a magic type slot.
            GCHandle gc = GCHandle.Alloc(impl);
            Marshal.WriteIntPtr(type, TypeOffset.magic(), (IntPtr)gc);

            // Set the handle attributes on the implementing instance.
            impl.tpHandle = Runtime.PyCLRMetaType;
            impl.gcHandle = gc;
            impl.pyHandle = type;

            //DebugUtil.DumpType(type);

            return type;
        }

        internal static IntPtr CreateSubType(IntPtr py_name, IntPtr py_base_type, IntPtr py_dict)
        {
            // Utility to create a subtype of a managed type with the ability for the
            // a python subtype able to override the managed implementation
            string name = Runtime.GetManagedString(py_name);

            // the derived class can have class attributes __assembly__ and __module__ which
            // control the name of the assembly and module the new type is created in.
            object assembly = null;
            object namespaceStr = null;

            List<PyObject> disposeList = new List<PyObject>();
            try
            {
                PyObject assemblyKey = new PyObject(Converter.ToPython("__assembly__", typeof(String)));
                disposeList.Add(assemblyKey);
                if (0 != Runtime.PyMapping_HasKey(py_dict, assemblyKey.Handle))
                {
                    PyObject pyAssembly = new PyObject(Runtime.PyDict_GetItem(py_dict, assemblyKey.Handle));
                    disposeList.Add(pyAssembly);
                    if (!Converter.ToManagedValue(pyAssembly.Handle, typeof(String), out assembly, false))
                        throw new InvalidCastException("Couldn't convert __assembly__ value to string");
                }

                PyObject namespaceKey = new PyObject(Converter.ToPythonImplicit("__namespace__"));
                disposeList.Add(namespaceKey);
                if (0 != Runtime.PyMapping_HasKey(py_dict, namespaceKey.Handle))
                {
                    PyObject pyNamespace = new PyObject(Runtime.PyDict_GetItem(py_dict, namespaceKey.Handle));
                    disposeList.Add(pyNamespace);
                    if (!Converter.ToManagedValue(pyNamespace.Handle, typeof(String), out namespaceStr, false))
                        throw new InvalidCastException("Couldn't convert __namespace__ value to string");
                }
            }
            finally
            {
                foreach (PyObject o in disposeList)
                    o.Dispose();
            }

            // create the new managed type subclassing the base managed type
            ClassBase baseClass = ManagedType.GetManagedObject(py_base_type) as ClassBase;
            if (null == baseClass)
            {
                return Exceptions.RaiseTypeError("invalid base class, expected CLR class type");
            }

            try
            {
                Type subType = ClassDerivedObject.CreateDerivedType(name,
                                                                    baseClass.type,
                                                                    py_dict,
                                                                    (string)namespaceStr,
                                                                    (string)assembly);

                // create the new ManagedType and python type
                ClassBase subClass = ClassManager.GetClass(subType);
                IntPtr py_type = GetTypeHandle(subClass, subType);

                // by default the class dict will have all the C# methods in it, but as this is a
                // derived class we want the python overrides in there instead if they exist.
                IntPtr cls_dict = Marshal.ReadIntPtr(py_type, TypeOffset.tp_dict);
                Runtime.PyDict_Update(cls_dict, py_dict);

                return py_type;
            }
            catch (Exception e)
            {
                return Exceptions.RaiseTypeError(e.Message);
            }
        }

        internal static IntPtr CreateMetaType(Type impl) {

            // The managed metatype is functionally little different than the
            // standard Python metatype (PyType_Type). It overrides certain of
            // the standard type slots, and has to subclass PyType_Type for 
            // certain functions in the C runtime to work correctly with it.

            IntPtr type = AllocateTypeObject("CLR Metatype");
            IntPtr py_type = Runtime.PyTypeType;

            Marshal.WriteIntPtr(type, TypeOffset.tp_base, py_type);
            Runtime.Incref(py_type);

            // Copy gc and other type slots from the base Python metatype.

            CopySlot(py_type, type, TypeOffset.tp_basicsize);
            CopySlot(py_type, type, TypeOffset.tp_itemsize);

            CopySlot(py_type, type, TypeOffset.tp_dictoffset);
            CopySlot(py_type, type, TypeOffset.tp_weaklistoffset);

            CopySlot(py_type, type, TypeOffset.tp_traverse);
            CopySlot(py_type, type, TypeOffset.tp_clear);
            CopySlot(py_type, type, TypeOffset.tp_is_gc);

            // Override type slots with those of the managed implementation.

            InitializeSlots(type, impl);

            int flags = TypeFlags.Default;
            flags |= TypeFlags.Managed;
            flags |= TypeFlags.HeapType;
            flags |= TypeFlags.HaveGC;
            Marshal.WriteIntPtr(type, TypeOffset.tp_flags, (IntPtr)flags);

            Runtime.PyType_Ready(type);

            IntPtr dict = Marshal.ReadIntPtr(type, TypeOffset.tp_dict);
            IntPtr mod = Runtime.PyString_FromString("CLR");
            Runtime.PyDict_SetItemString(dict, "__module__", mod);

            //DebugUtil.DumpType(type);

            return type;
        }


        internal static IntPtr BasicSubType(string name, IntPtr base_,
                                            Type impl) {

            // Utility to create a subtype of a std Python type, but with
            // a managed type able to override implementation

            IntPtr type = AllocateTypeObject(name);
            //Marshal.WriteIntPtr(type, TypeOffset.tp_basicsize, (IntPtr)obSize);
            //Marshal.WriteIntPtr(type, TypeOffset.tp_itemsize, IntPtr.Zero);

            //IntPtr offset = (IntPtr)ObjectOffset.ob_dict;
            //Marshal.WriteIntPtr(type, TypeOffset.tp_dictoffset, offset);

            //IntPtr dc = Runtime.PyDict_Copy(dict);
            //Marshal.WriteIntPtr(type, TypeOffset.tp_dict, dc);

            Marshal.WriteIntPtr(type, TypeOffset.tp_base, base_);
            Runtime.Incref(base_);

            int flags = TypeFlags.Default;
            flags |= TypeFlags.Managed;
            flags |= TypeFlags.HeapType;
            flags |= TypeFlags.HaveGC;
            Marshal.WriteIntPtr(type, TypeOffset.tp_flags, (IntPtr)flags);

            CopySlot(base_, type, TypeOffset.tp_traverse);
            CopySlot(base_, type, TypeOffset.tp_clear);
            CopySlot(base_, type, TypeOffset.tp_is_gc);

            InitializeSlots(type, impl);

            Runtime.PyType_Ready(type);

            IntPtr tp_dict = Marshal.ReadIntPtr(type, TypeOffset.tp_dict);
            IntPtr mod = Runtime.PyString_FromString("CLR");
            Runtime.PyDict_SetItemString(tp_dict, "__module__", mod);

            return type;
        }


        //====================================================================
        // Utility method to allocate a type object & do basic initialization.
        //====================================================================

        internal static IntPtr AllocateTypeObject(string name) {
            IntPtr type = Runtime.PyType_GenericAlloc(Runtime.PyTypeType, 0);

            // Cheat a little: we'll set tp_name to the internal char * of
            // the Python version of the type name - otherwise we'd have to
            // allocate the tp_name and would have no way to free it.
#if (PYTHON32 || PYTHON33 || PYTHON34 || PYTHON35)
            // For python3 we leak two objects. One for the ascii representation
            // required for tp_name, and another for the unicode representation
            // for ht_name.
            IntPtr temp = Runtime.PyBytes_FromString(name);
            IntPtr raw = Runtime.PyBytes_AS_STRING(temp);
            temp = Runtime.PyUnicode_FromString(name);
#else
            IntPtr temp = Runtime.PyString_FromString(name);
            IntPtr raw = Runtime.PyString_AS_STRING(temp);
#endif
            Marshal.WriteIntPtr(type, TypeOffset.tp_name, raw);
            Marshal.WriteIntPtr(type, TypeOffset.name, temp);

#if (PYTHON33 || PYTHON34 || PYTHON35)
            Marshal.WriteIntPtr(type, TypeOffset.qualname, temp);
#endif

            long ptr = type.ToInt64(); // 64-bit safe

            temp = new IntPtr(ptr + TypeOffset.nb_add);
            Marshal.WriteIntPtr(type, TypeOffset.tp_as_number, temp);

            temp = new IntPtr(ptr + TypeOffset.sq_length);
            Marshal.WriteIntPtr(type, TypeOffset.tp_as_sequence, temp);

            temp = new IntPtr(ptr + TypeOffset.mp_length);
            Marshal.WriteIntPtr(type, TypeOffset.tp_as_mapping, temp);

#if (PYTHON32 || PYTHON33 || PYTHON34 || PYTHON35)
            temp = new IntPtr(ptr + TypeOffset.bf_getbuffer);
            Marshal.WriteIntPtr(type, TypeOffset.tp_as_buffer, temp);
#else
            temp = new IntPtr(ptr + TypeOffset.bf_getreadbuffer);
            Marshal.WriteIntPtr(type, TypeOffset.tp_as_buffer, temp);
#endif

#if(PYTHON35)
            temp = new IntPtr(ptr + TypeOffset.am_await);
            Marshal.WriteIntPtr(type, TypeOffset.tp_as_async, temp);
#endif

            return type;
        }


        //====================================================================
        // Given a newly allocated Python type object and a managed Type that
        // provides the implementation for the type, connect the type slots of
        // the Python object to the managed methods of the implementing Type.
        //====================================================================

        internal static void InitializeSlots(IntPtr type, Type impl) {
            Hashtable seen = new Hashtable(8);
            Type offsetType = typeof(TypeOffset);

            while (impl != null) {
                MethodInfo[] methods = impl.GetMethods(tbFlags);
                for (int i = 0; i < methods.Length; i++) {
                    MethodInfo method = methods[i];
                    string name = method.Name;
                    if (! (name.StartsWith("tp_") ||
                           name.StartsWith("nb_") ||
                           name.StartsWith("sq_") ||
                           name.StartsWith("mp_") ||
                           name.StartsWith("bf_")
                           ) ) {
                        continue;
                    }

                    if (seen[name] != null) {
                        continue;
                    }
                    
                    FieldInfo fi = offsetType.GetField(name);
                    int offset = (int)fi.GetValue(offsetType);

                    IntPtr slot = Interop.GetThunk(method);
                    Marshal.WriteIntPtr(type, offset, slot);

                    seen[name] = 1;
                }

                impl = impl.BaseType;
            }

        }


        //====================================================================
        // Given a newly allocated Python type object and a managed Type that
        // implements it, initialize any methods defined by the Type that need
        // to appear in the Python type __dict__ (based on custom attribute).
        //====================================================================

        private static void InitMethods(IntPtr pytype, Type type) {
            IntPtr dict = Marshal.ReadIntPtr(pytype, TypeOffset.tp_dict);
            Type marker = typeof(PythonMethodAttribute);

            BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            HashSet<string> addedMethods = new HashSet<string>();

            while (type != null) {
                MethodInfo[] methods = type.GetMethods(flags);
                for (int i = 0; i < methods.Length; i++) {
                    MethodInfo method = methods[i];
                    if (!addedMethods.Contains(method.Name)) {
                        object[] attrs = method.GetCustomAttributes(marker, false);
                        if (attrs.Length > 0) {
                            string method_name = method.Name;
                            MethodInfo[] mi = new MethodInfo[1];
                            mi[0] = method;
                            MethodObject m = new TypeMethod(type, method_name, mi);
                            Runtime.PyDict_SetItemString(dict, method_name, m.pyHandle);
                            addedMethods.Add(method_name);
                        }
                    }
                }
                type = type.BaseType;
            }
        }


        //====================================================================
        // Utility method to copy slots from a given type to another type.
        //====================================================================

        internal static void CopySlot(IntPtr from, IntPtr to, int offset) {
            IntPtr fp = Marshal.ReadIntPtr(from, offset);
            Marshal.WriteIntPtr(to, offset, fp);
        }


    }


}
