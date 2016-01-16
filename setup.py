"""
Setup script for building clr.pyd and dependencies using mono and into
an egg or wheel.
"""
from setuptools import setup, Extension
from distutils.command.build_ext import build_ext
from distutils.command.install_lib import install_lib
from distutils.command.install_data import install_data
from distutils.sysconfig import get_config_var
from platform import architecture
from subprocess import Popen, CalledProcessError, PIPE, check_call
from glob import glob
import fnmatch
import sys
import os

CONFIG = "Release"  # Release or Debug
DEVTOOLS = "MsDev" if sys.platform == "win32" else "Mono"
VERBOSITY = "minimal"  # quiet, minimal, normal, detailed, diagnostic
PLATFORM = "x64" if architecture()[0] == "64bit" else "x86"


def _find_msbuild_tool(tool="msbuild.exe", use_windows_sdk=False):
    """Return full path to one of the microsoft build tools"""
    try:
        import _winreg
    except ImportError:
        import winreg as _winreg

    if use_windows_sdk:
        value_name = "InstallationFolder"
        sdk_name = "Windows SDK"
        keys_to_check = [
            r"SOFTWARE\Microsoft\Microsoft SDKs\Windows\v7.1A\WinSDK-Win32Tools",
            r"SOFTWARE\Microsoft\Microsoft SDKs\Windows\v7.1\WinSDKWin32Tools",
            r"SOFTWARE\Microsoft\Microsoft SDKs\Windows\v7.0A\WinSDK-Win32Tools",
            r"SOFTWARE\Microsoft\Microsoft SDKs\Windows\v7.0\WinSDKWin32Tools",
            r"SOFTWARE\Microsoft\Microsoft SDKs\Windows\v6.0A\WinSDKWin32Tools",
        ]
    else:
        value_name = "MSBuildToolsPath"
        sdk_name = "MSBuild"
        keys_to_check = [
            r"SOFTWARE\Microsoft\MSBuild\ToolsVersions\14.0",
            r"SOFTWARE\Microsoft\MSBuild\ToolsVersions\12.0",
            r"SOFTWARE\Microsoft\MSBuild\ToolsVersions\4.0",
            r"SOFTWARE\Microsoft\MSBuild\ToolsVersions\3.5",
            r"SOFTWARE\Microsoft\MSBuild\ToolsVersions\2.0"
        ]

    hreg = _winreg.ConnectRegistry(None, _winreg.HKEY_LOCAL_MACHINE)
    try:
        hkey = None
        for key in keys_to_check:
            try:
                hkey = _winreg.OpenKey(hreg, key)
                break
            except WindowsError:
                pass

        if hkey is None:
            raise RuntimeError("%s could not be found" % sdk_name)

        try:
            val, type_ = _winreg.QueryValueEx(hkey, value_name)
            if type_ != _winreg.REG_SZ:
                raise RuntimeError("%s could not be found" % sdk_name)
 
            path = os.path.join(val, tool)
            if os.path.exists(path):
                return path
        finally:
            hkey.Close()
    finally:
        hreg.Close()

    raise RuntimeError("%s could not be found" % tool)
    

if DEVTOOLS == "MsDev":
    _xbuild = "\"%s\"" % _find_msbuild_tool("msbuild.exe")
    _defines_sep = ";"
    _config = "%sWin" % CONFIG

elif DEVTOOLS == "Mono":
    _xbuild = "xbuild"
    _defines_sep = ","
    _config = "%sMono" % CONFIG

else:
    raise NotImplementedError("DevTools %s not supported (use MsDev or Mono)" % DEVTOOLS)


class PythonNET_BuildExt(build_ext):

    def build_extension(self, ext):
        """
        Builds the .pyd file using msbuild or xbuild.
        """
        if ext.name != "clr":
            return build_ext.build_extension(self, ext)

        # install packages using nuget
        self._install_packages()

        dest_file = self.get_ext_fullpath(ext.name)
        dest_dir = os.path.dirname(dest_file)
        if not os.path.exists(dest_dir):
            os.makedirs(dest_dir)

        # Up to Python 3.2 sys.maxunicode is used to determine the size of Py_UNICODE
        # but from 3.3 onwards Py_UNICODE is a typedef of wchar_t.
        if sys.version_info[:2] <= (3, 2):
            unicode_width = 2 if sys.maxunicode < 0x10FFFF else 4
        else:
            import ctypes
            unicode_width = ctypes.sizeof(ctypes.c_wchar)

        defines = [
            "PYTHON%d%s" % (sys.version_info[:2]),
            "UCS%d" % unicode_width,
        ]

        if CONFIG == "Debug":
            defines.extend(["DEBUG", "TRACE"])

        if sys.platform != "win32" and DEVTOOLS == "Mono":
            if sys.platform == "darwin":
                defines.append("MONO_OSX")
            else:
                defines.append("MONO_LINUX")

            # Check if --enable-shared was set when Python was built
            enable_shared = get_config_var("Py_ENABLE_SHARED")
            if enable_shared == 0:
                defines.append("PYTHON_WITHOUT_ENABLE_SHARED")

        if hasattr(sys, "abiflags"):
            if "d" in sys.abiflags:
                defines.append("PYTHON_WITH_PYDEBUG")
            if "m" in sys.abiflags:
                defines.append("PYTHON_WITH_PYMALLOC")
            if "u" in sys.abiflags:
                defines.append("PYTHON_WITH_WIDE_UNICODE")

        cmd = [
            _xbuild,
            "pythonnet.sln",
            "/p:Configuration=%s" % _config,
            "/p:Platform=%s" % PLATFORM,
            "/p:DefineConstants=\"%s\"" % _defines_sep.join(defines),
            "/p:PythonBuildDir=%s" % os.path.abspath(dest_dir),
            "/verbosity:%s" % VERBOSITY,
        ]

        manifest = self._get_manifest(dest_dir)
        if manifest:
            cmd.append("/p:PythonManifest=\"%s\"" % manifest)

        self.announce("Building: %s" % " ".join(cmd))
        use_shell = True if DEVTOOLS == "Mono" else False
        check_call(" ".join(cmd + ["/t:Clean"]), shell=use_shell)
        check_call(" ".join(cmd + ["/t:Build"]), shell=use_shell)

        if DEVTOOLS == "Mono":
            self._build_monoclr(ext)

    def _get_manifest(self, build_dir):
        if DEVTOOLS == "MsDev" and sys.version_info[:2] > (2,5):
            mt = _find_msbuild_tool("mt.exe", use_windows_sdk=True)
            manifest = os.path.abspath(os.path.join(build_dir, "app.manifest"))
            cmd = [mt, '-inputresource:"%s"' % sys.executable, '-out:"%s"' % manifest]
            self.announce("Extracting manifest from %s" % sys.executable)
            check_call(" ".join(cmd), shell=False)
            return manifest

    def _build_monoclr(self, ext):
        mono_libs = _check_output("pkg-config --libs mono-2", shell=True)
        mono_cflags = _check_output("pkg-config --cflags mono-2", shell=True)
        glib_libs = _check_output("pkg-config --libs glib-2.0", shell=True)
        glib_cflags = _check_output("pkg-config --cflags glib-2.0", shell=True)
        cflags = mono_cflags.strip() + " " + glib_cflags.strip()
        libs = mono_libs.strip() + " " + glib_libs.strip()

        # build the clr python module
        clr_ext = Extension("clr",
                    sources=[
                        "src/monoclr/pynetinit.c",
                        "src/monoclr/clrmod.c"
                    ],
                    extra_compile_args=cflags.split(" "),
                    extra_link_args=libs.split(" "))

        build_ext.build_extension(self, clr_ext)

    def _install_packages(self):
        """install packages using nuget"""
        nuget = os.path.join("tools", "nuget", "nuget.exe")
        use_shell = False
        if DEVTOOLS == "Mono":
            nuget = "mono %s" % nuget
            use_shell = True

        cmd = "%s restore pythonnet.sln -o packages" % nuget
        self.announce("Installing packages: %s" % cmd)
        check_call(cmd, shell=use_shell)


class PythonNET_InstallLib(install_lib):

    def install(self):
        if not os.path.isdir(self.build_dir):
            self.warn("'%s' does not exist -- no Python modules to install" %
                        self.build_dir)
            return

        if not os.path.exists(self.install_dir):
            self.mkpath(self.install_dir)

        # only copy clr.pyd/.so
        for srcfile in glob(os.path.join(self.build_dir, "clr.*")):
            destfile = os.path.join(self.install_dir, os.path.basename(srcfile))
            self.copy_file(srcfile, destfile)


class PythonNET_InstallData(install_data):

    def run(self):
        build_cmd = self.get_finalized_command("build_ext")
        install_cmd = self.get_finalized_command("install")
        build_lib = os.path.abspath(build_cmd.build_lib)
        install_platlib = os.path.relpath(install_cmd.install_platlib, self.install_dir)

        for i, data_files in enumerate(self.data_files):
            if isinstance(data_files, str):
                self.data_files[i] = data_files[i].format(build_lib=build_lib)
            else:
                for j, filename in enumerate(data_files[1]):
                    data_files[1][j] = filename.format(build_lib=build_lib)
                dest = data_files[0].format(install_platlib=install_platlib)
                self.data_files[i] = dest, data_files[1]

        return install_data.run(self)


def _check_output(*popenargs, **kwargs):
    """subprocess.check_output from python 2.7.
    Added here to support building for earlier versions
    of Python.
    """
    process = Popen(stdout=PIPE, *popenargs, **kwargs)
    output, unused_err = process.communicate()
    retcode = process.poll()
    if retcode:
        cmd = kwargs.get("args")
        if cmd is None:
            cmd = popenargs[0]
        raise CalledProcessError(retcode, cmd, output=output)
    if sys.version_info[0] > 2:
        return output.decode("ascii")
    return output


if __name__ == "__main__":
    setupdir = os.path.dirname(__file__)
    if setupdir:
        os.chdir(setupdir)

    sources = []
    for ext in (".sln", ".snk", ".config"):
        sources.extend(glob("*" + ext))

    for root, dirnames, filenames in os.walk("src"):
        for ext in (".cs", ".csproj", ".sln", ".snk", ".config", ".il", ".py", ".c", ".h", ".ico"):
            for filename in fnmatch.filter(filenames, "*" + ext):
                sources.append(os.path.join(root, filename))

    for root, dirnames, filenames in os.walk("tools"):
        for ext in (".exe"):
            for filename in fnmatch.filter(filenames, "*" + ext):
                sources.append(os.path.join(root, filename))

    setup(
        name="pythonnet",
        version="2.1.1",
        description=".Net and Mono integration for Python",
        url='http://pythonnet.github.io/',
        author="Python for .Net developers",
        classifiers=[
            'Programming Language :: Python :: 2.6',
            'Programming Language :: Python :: 2.7',
            'Programming Language :: Python :: 3.2',
            'Programming Language :: Python :: 3.3',
            'Programming Language :: Python :: 3.4',
            'Programming Language :: Python :: 3.5',
            'Programming Language :: C#',
            'License :: OSI Approved :: Zope Public License',
            'Development Status :: 5 - Production/Stable',
            'Intended Audience :: Developers',
            'Operating System :: Microsoft :: Windows',
            'Operating System :: POSIX :: Linux',
            'Operating System :: MacOS :: MacOS X',
        ],
        ext_modules=[
            Extension("clr", sources=sources)
        ],
        data_files=[
            ("{install_platlib}", [
                "{build_lib}/Python.Runtime.dll",
                "Python.Runtime.dll.config"]),
        ],
        zip_safe=False,
        cmdclass={
            "build_ext" : PythonNET_BuildExt,
            "install_lib" : PythonNET_InstallLib,
            "install_data": PythonNET_InstallData,
        }
    )

