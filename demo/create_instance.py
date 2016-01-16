# ===========================================================================
# This software is subject to the provisions of the Zope Public License,
# Version 2.0 (ZPL).  A copy of the ZPL should accompany this distribution.
# THIS SOFTWARE IS PROVIDED "AS IS" AND ANY AND ALL EXPRESS OR IMPLIED
# WARRANTIES ARE DISCLAIMED, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
# WARRANTIES OF TITLE, MERCHANTABILITY, AGAINST INFRINGEMENT, AND FITNESS
# FOR A PARTICULAR PURPOSE.
# ===========================================================================

print("-------------------------------------")
import numpy;dest = numpy.empty(100, dtype=float)
import clr
from System import IntPtr
x = dest.__array_interface__['data'][0]
#to = IntPtr.__overloads__[int](x)      
#print(to)
#to2 = clr.IntPtr_int(x)
#print(to2)
to3 = clr.IntPtr_long(x)
print(to3)
print("-------------------------------------")


import sys
import os
import numpy
exe = os.path.dirname(sys.executable)
os.environ["PATH"]="{0};{0}\\Scripts;{1}".format(exe, os.environ["PATH"])
print("*********", exe)
import clr
print("*********", "1")
print(clr.Print_int(5))
print("*********", "1a")
print(clr)
print("*********", "2")
from System import Array
print("*********", "3")
n = 100
r = Array.CreateInstance(float, n)
print("*********", "4")
print(list(r))
print("*********", "5")
from System import Random, Double, IntPtr
print("*********", "6")
r = Random()
src = Array.CreateInstance(Double, n)
for i in range(len(src)):
    src[i] = r.NextDouble()
print("*********", "7")
from System.Runtime.InteropServices import Marshal
print("*********", "8")
        
def array2numpy(cs_array):
    print("[array2numpy]", cs_array, str(cs_array))
    #print("[array2numpy]", type(cs_array))
    if "System.Single[]" in str(str(cs_array)):
        dtype = numpy.float32
    else:
        dtype = float
    print("[array2numpy]", dtype)
    dest = numpy.empty(len(cs_array), dtype=dtype)
    print("[array2numpy]")
    print(dest.__array_interface__)
    x = dest.__array_interface__['data'][0]
    print(x)
    to = IntPtr.__overloads__[int](x)    
    print("[array2numpy]")
    Marshal.Copy(cs_array, 0, to, len(cs_array))    
    print("[array2numpy]")
    return dest
    
def numpy2array(array):
    if array.dtype == numpy.float32:
        typ = Float
    else:
        typ = Double
    array = numpy.ascontiguousarray(array)
    dest = Array.CreateInstance(typ, array.size)
    src = IntPtr.__overloads__[int](array.__array_interface__['data'][0])  
    Marshal.Copy(src, dest, 0, array.size)
    return dest
  
    
print("*********", "11")
npa = array2numpy(src)
print("*********", "12")
csa2 = numpy2array(npa)
print("*********", "13")
