"""Arma la carpeta portable del consultor académico (para la PC o un pendrive).

Uso, con el Python del venv del consultor (de ahí sale el runtime):
    ..\\..\\CSJN-PROYECTO\\CSJN-SCRIPTS\\venv\\Scripts\\python.exe portable\\build_portable.py [--out RUTA] [--sin-verificar]

Resultado (por defecto en dist\\CONSULTOR-ACADEMICO del repo):
    CONSULTOR-ACADEMICO.exe   lanzador: abre app\\CONSULTOR-ACADEMICO-GUI.exe
    app\\       la GUI publicada con su propio .NET, backend\\main.py, scripts\\ (ACADEMICO-SCRIPTS sin tests),
                diagnostico.py, actualizar.py y VERSION
    runtime\\   Python 3.10 propio con los paquetes del venv (torch con CUDA, faiss, sentence-transformers, fastapi...)
    bin\\       llama-server.exe con sus DLL (CUDA) y pdftotext.exe
    data\\      configuración, índice y sesiones de esta copia (no se toca al rearmar)
    Models\\    modelos de esta copia (opcional; no se toca al rearmar)
    portable.flag, VERSION.txt, LEEME.txt, Diagnostico.bat, Actualizar.bat

--solo-app rearma solo app\\ (y los archivos de la raíz) de una carpeta ya armada. publicar.py usa fill_app() para
armar el zip de la release.

No copia modelos ni PDF: los modelos se buscan en Models\\ y en <unidad>\\MODELS, y los PDF en <unidad>\\BASE
(ver Entorno.cs).

Seguridad: app\\, runtime\\ y bin\\ llevan un archivo marcador; solo se reemplazan si lo tienen. Si existe una carpeta
con ese nombre SIN marcador, el script se detiene en vez de pisarla. data\\ y Models\\ nunca se borran ni se reemplazan.
"""
import argparse
import fnmatch
import glob
import os
import re
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
CSJN = os.path.dirname(REPO)
CSPROJ = os.path.join(REPO, "CONSULTOR-ACADEMICO-GUI.csproj")
SCRIPTS = os.path.join(CSJN, "ACADEMICO-PROYECTO", "ACADEMICO-SCRIPTS")
VENV = os.path.join(CSJN, "CSJN-PROYECTO", "CSJN-SCRIPTS", "venv")
PDFTOTEXT = os.path.join(CSJN, "CSJN-PROYECTO", "CSJN-SCRIPTS", "pdftotext.exe")
CSC = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe")
MARKER = ".build_portable"

PYTHON_ROOT_FILES = ("python.exe", "pythonw.exe", "python310.dll", "python3.dll", "vcruntime140.dll",
                     "vcruntime140_1.dll", "LICENSE.txt")
LIB_EXCLUDE_DIRS = {"site-packages", "test", "tkinter", "idlelib", "turtledemo", "ensurepip", "venv", "pydoc_data"}
SITE_EXCLUDE = ("pip", "pip-*.dist-info")
DLL_EXCLUDE = ("_test*", "_ctypes_test*", "*.ico", "*.cat", "_tkinter*", "tcl*.dll", "tk*.dll")
LLAMA_KEEP = ("llama-server.exe", "llama-server-impl.dll", "llama.dll", "llama-common.dll", "mtmd.dll", "ggml*.dll",
              "libomp*.dll", "msvcp140*.dll", "vcruntime140*.dll", "cublas*.dll", "cudart*.dll", "LICENSE*")
VC_RUNTIME = ("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll")
# Primero el build con CUDA: con una placa NVIDIA el modelo corre en la GPU; sin ella ggml-cuda.dll no carga y queda
# la CPU. Los demás solo se usan para tomar el runtime de VC++, que el build CUDA oficial no trae.
LLAMA_CANDIDATES = (
    r"E:\llama-server",
    r"C:\llama-server",
    r"F:\source\repos\USBagent\bin",
)


def say(msg):
    print(msg, flush=True)


def die(msg):
    print("ERROR: " + msg, file=sys.stderr, flush=True)
    sys.exit(1)


def rmtree_forced(path):
    def onerr(func, p, _exc):
        os.chmod(p, 0o700)
        func(p)
    shutil.rmtree(path, onerror=onerr)


def is_managed(path):
    return os.path.isfile(os.path.join(path, MARKER))


def replace_managed(dest, fill):
    """Llena dest.new con fill(carpeta) y lo cambia por dest, solo si dest no existe o es nuestro (tiene marcador)."""
    if os.path.exists(dest) and not is_managed(dest):
        die(f"{dest} existe y no tiene el marcador {MARKER}: no lo creó este script, no se toca.")
    new, old = dest + ".new", dest + ".old"
    for leftover in (new, old):
        if os.path.exists(leftover):
            if not is_managed(leftover):
                die(f"{leftover} existe y no es de este script: no se toca.")
            rmtree_forced(leftover)
    os.makedirs(new)
    with open(os.path.join(new, MARKER), "w", encoding="utf-8") as f:
        f.write("Carpeta generada por build_portable.py; se reemplaza entera al rearmar. No pongas nada tuyo acá.\n")
    fill(new)
    if os.path.exists(dest):
        os.rename(dest, old)
    try:
        os.rename(new, dest)
    except OSError:
        if os.path.exists(old):
            os.rename(old, dest)
        raise
    if os.path.exists(old):
        rmtree_forced(old)


def matches(name, patterns):
    return any(fnmatch.fnmatch(name.lower(), pat.lower()) for pat in patterns)


def copy_matching(src_dir, dst_dir, patterns=None, exclude=()):
    os.makedirs(dst_dir, exist_ok=True)
    n = 0
    for name in sorted(os.listdir(src_dir)):
        p = os.path.join(src_dir, name)
        if not os.path.isfile(p):
            continue
        if patterns and not matches(name, patterns):
            continue
        if matches(name, exclude):
            continue
        shutil.copy2(p, os.path.join(dst_dir, name))
        n += 1
    return n


def find_ucrt():
    base = os.path.join(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"), "Windows Kits", "10", "Redist")
    hits = sorted(glob.glob(os.path.join(base, "*", "ucrt", "DLLs", "x64")), reverse=True)
    return hits[0] if hits else None


def copy_ucrt(ucrt, dst):
    return copy_matching(ucrt, dst, patterns=("api-ms-win-crt-*.dll", "ucrtbase.dll"))


def leer_version():
    with open(CSPROJ, encoding="utf-8-sig") as f:
        m = re.search(r"<Version>([^<]+)</Version>", f.read())
    if not m:
        die(f"{CSPROJ} no tiene <Version>")
    return m.group(1).strip()


def fill_app(dst):
    r = subprocess.run(["dotnet", "publish", CSPROJ, "-nologo", "-c", "Release", "-r", "win-x64",
                        "--self-contained", "true", "-o", dst],
                       capture_output=True, text=True, encoding="oem", errors="replace")
    if r.returncode != 0 or not os.path.isfile(os.path.join(dst, "CONSULTOR-ACADEMICO-GUI.exe")):
        die(f"dotnet publish falló (código {r.returncode}):\n{(r.stdout + r.stderr).strip()[-3000:]}")
    n_gui = sum(len(fs) for _dp, _d, fs in os.walk(dst))
    os.makedirs(os.path.join(dst, "backend"))
    shutil.copy2(os.path.join(REPO, "BACKEND", "main.py"), os.path.join(dst, "backend", "main.py"))
    n_scripts = copy_matching(SCRIPTS, os.path.join(dst, "scripts"), patterns=("*.py",), exclude=("_test*",))
    shutil.copy2(os.path.join(HERE, "diagnostico.py"), os.path.join(dst, "diagnostico.py"))
    shutil.copy2(os.path.join(HERE, "actualizar.py"), os.path.join(dst, "actualizar.py"))
    # La versión de app\: la compara actualizar.py con la etiqueta de la release.
    write_text(os.path.join(dst, "VERSION"), leer_version() + "\r\n")
    say(f"  app: GUI autónoma ({n_gui} archivos), backend\\main.py, scripts\\ ({n_scripts} .py), diagnostico.py, "
        "actualizar.py")


def revisar_site_packages(site):
    """Un .pth o .egg-link con una ruta absoluta haría que el runtime dependa de una carpeta de esta PC."""
    for name in os.listdir(site):
        p = os.path.join(site, name)
        if name.lower().endswith(".egg-link"):
            die(f"{p}: paquete instalado en modo editable, apunta fuera del venv")
        if name.lower().endswith(".pth"):
            with open(p, encoding="utf-8", errors="replace") as f:
                for line in f:
                    line = line.strip()
                    if line and not line.startswith(("#", "import ", "import\t")) and os.path.isabs(line):
                        die(f"{p} agrega una ruta absoluta ({line}): el runtime dependería de esta PC")


def make_runtime_filler(py, site, ucrt):
    def fill(dst):
        for name in PYTHON_ROOT_FILES:
            src = os.path.join(py, name)
            if not os.path.isfile(src):
                die(f"falta {src} en la instalación de Python origen")
            shutil.copy2(src, dst)
        n = copy_matching(os.path.join(py, "DLLs"), os.path.join(dst, "DLLs"), exclude=DLL_EXCLUDE)
        lib_src, lib_dst = os.path.join(py, "Lib"), os.path.join(dst, "Lib")
        shutil.copytree(lib_src, lib_dst, ignore=lambda d, names: [x for x in names if x in LIB_EXCLUDE_DIRS and os.path.normcase(d) == os.path.normcase(lib_src)])
        shutil.copytree(site, os.path.join(lib_dst, "site-packages"),
                        ignore=lambda d, names: [x for x in names if matches(x, SITE_EXCLUDE) and os.path.normcase(d) == os.path.normcase(site)])
        if ucrt:
            copy_ucrt(ucrt, dst)
        n_paq = len([x for x in os.listdir(os.path.join(lib_dst, "site-packages")) if x.endswith(".dist-info")])
        say(f"  runtime: {n} DLL/pyd, Lib recortada, {n_paq} paquetes del venv (sin pip)"
            + (", UCRT local" if ucrt else ", SIN UCRT local"))
    return fill


def make_bin_filler(llama, ucrt):
    def fill(dst):
        n = copy_matching(llama, dst, patterns=LLAMA_KEEP)
        if not os.path.isfile(os.path.join(dst, "llama-server.exe")):
            die(f"no hay llama-server.exe en {llama}")
        for dll in VC_RUNTIME:
            if not os.path.isfile(os.path.join(dst, dll)):
                orig = next((os.path.join(c, dll) for c in LLAMA_CANDIDATES if os.path.isfile(os.path.join(c, dll))), None)
                if orig:
                    shutil.copy2(orig, os.path.join(dst, dll))
                    n += 1
                else:
                    say(f"  AVISO: bin\\ sin {dll}; depende del runtime de VC++ del equipo destino.")
        if not os.path.isfile(PDFTOTEXT):
            die(f"no está {PDFTOTEXT}")
        shutil.copy2(PDFTOTEXT, os.path.join(dst, "pdftotext.exe"))
        if ucrt:
            copy_ucrt(ucrt, dst)
        cuda = "con CUDA (usa la GPU NVIDIA si hay; si no, la CPU)" if os.path.isfile(os.path.join(dst, "ggml-cuda.dll")) else "solo CPU"
        say(f"  bin: {n} archivos de llama.cpp de {llama}, {cuda}; pdftotext.exe")
    return fill


def build_launcher(out):
    if not os.path.isfile(CSC):
        die(f"no está {CSC} (.NET Framework 4): no se puede compilar el lanzador")
    exe = os.path.join(out, "CONSULTOR-ACADEMICO.exe")
    icono = os.path.join(REPO, "balanza5.ico")   # el mismo de la GUI (<ApplicationIcon> del .csproj)
    r = subprocess.run([CSC, "/nologo", "/target:winexe", "/optimize+", "/codepage:65001", f"/out:{exe}",
                        f"/win32icon:{icono}", "/r:System.Windows.Forms.dll", os.path.join(HERE, "Lanzador.cs")],
                       capture_output=True, text=True, encoding="oem", errors="replace")
    if r.returncode != 0 or not os.path.isfile(exe):
        die(f"no se pudo compilar el lanzador (código {r.returncode}):\n{(r.stdout + r.stderr).strip()}")
    say(f"  CONSULTOR-ACADEMICO.exe: {os.path.getsize(exe) // 1024} KB")


DIAGNOSTICO = ("@echo off\r\n\"%~dp0runtime\\python.exe\" -I \"%~dp0app\\diagnostico.py\" --completo\r\n"
               "echo.\r\necho El informe quedo en la carpeta data\\diagnostico.txt\r\npause\r\n")

ACTUALIZAR = ("@echo off\r\ncd /d \"%~dp0\"\r\n\"%~dp0runtime\\python.exe\" -I \"%~dp0app\\actualizar.py\" %*\r\n"
              "echo.\r\npause\r\n")

LEEME = """Consultor Academico portable
============================

Doble clic en CONSULTOR-ACADEMICO.exe. La version esta en VERSION.txt.

Que hay en esta carpeta
- app\\       el programa (la ventana, el backend y los scripts de busqueda e ingesta)
- runtime\\   Python propio con todo lo que el backend necesita; no usa el Python del equipo
- bin\\       llama-server (el modelo de lenguaje local) y pdftotext
- data\\      todo lo que el programa guarda: configuracion, indice de documentos, sesiones, registros.
             Es propio de esta copia: la de la PC y la del pendrive tienen cada una el suyo.
- Models\\    opcional: modelos solo para esta copia

Modelos (no vienen incluidos: son los archivos pesados)
- Qwen3.5-9B-Q4_K_M.gguf          el modelo de lenguaje
- multilingual-e5-large\\          carpeta del modelo de embeddings (la que tiene modules.json)
Se buscan, en este orden: la carpeta elegida en Configuracion, Models\\ de esta carpeta y MODELS\\ en la raiz de
la unidad (en el pendrive, G:\\MODELS si la unidad es G:). Pueden estar en subcarpetas (hasta tres niveles).

Biblioteca de PDF
- Por defecto es BASE\\ en la raiz de la unidad (G:\\BASE en el pendrive), o la carpeta elegida en Configuracion.
- Los PDF que estan dentro de la biblioteca se guardan en el indice con su ruta relativa a ella: si el pendrive
  cambia de letra en otra PC, siguen encontrandose.
- El programa no copia los PDF: indexa el archivo donde esta.

Si algo no arranca
- Diagnostico.bat revisa runtime\\, bin\\, app\\ y los modelos, y guarda un informe en data\\diagnostico.txt.
  Cada linea dice OK, AVISO o FALLA.

Actualizar
- El boton "Actualizar" de la ventana, o Actualizar.bat: baja de GitHub la ultima version publicada y reemplaza
  app\\. data\\, Models\\, runtime\\ y bin\\ no se tocan.
- Actualizar.bat 1.0.0 instala esa version (por ejemplo, para volver a una anterior).

Rearmar
- app\\, runtime\\ y bin\\ se regeneran con portable\\build_portable.py del repo CONSULTOR-ACADEMICO-GUI.
  data\\ y Models\\ son tuyos y el script nunca los toca.

Limites conocidos
- Sin placa NVIDIA, el modelo de lenguaje y los embeddings corren en la CPU: funciona, pero mas lento.
- El modelo de lenguaje necesita unos 6 GB de memoria libre (de la placa o de la RAM).
"""

MODELS_README = ("Modelos solo para esta copia (opcional).\r\n"
                 "El programa busca aqui, y en MODELS\\ de la raiz de la unidad:\r\n"
                 "  Qwen3.5-9B-Q4_K_M.gguf\r\n"
                 "  multilingual-e5-large\\  (la carpeta del modelo, con modules.json)\r\n")


def write_text(path, text):
    with open(path, "w", encoding="ascii", newline="") as f:
        f.write(text)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=os.path.join(REPO, "dist", "CONSULTOR-ACADEMICO"))
    ap.add_argument("--llama-bin", default="", help="carpeta con llama-server.exe (por defecto la primera de LLAMA_CANDIDATES)")
    ap.add_argument("--sin-ucrt", action="store_true", help="no copiar el UCRT local (Windows 10 y 11 ya lo traen)")
    ap.add_argument("--sin-verificar", action="store_true", help="no correr el diagnóstico sobre lo armado")
    ap.add_argument("--solo-app", action="store_true",
                    help="rearmar solo app\\ (y los archivos de la raíz) de una carpeta ya armada; runtime\\ y bin\\ no se tocan")
    a = ap.parse_args()

    out = os.path.abspath(a.out)
    if a.solo_app:
        if not (is_managed(os.path.join(out, "runtime")) and is_managed(os.path.join(out, "bin"))):
            die(f"--solo-app necesita una carpeta ya armada (con runtime\\ y bin\\): {out}")
        say(f"Versión: {leer_version()}\nDestino: {out} (solo app\\)")
        replace_managed(os.path.join(out, "app"), fill_app)
        escribir_raiz(out, leer_version())
        verificar(out, a.sin_verificar)
        return
    py = sys.base_prefix
    site = os.path.join(VENV, "Lib", "site-packages")
    if os.path.normcase(os.path.abspath(sys.prefix)) != os.path.normcase(os.path.abspath(VENV)):
        die(f"correlo con el Python del venv ({VENV}\\Scripts\\python.exe): de ahí salen los paquetes del runtime")
    if sys.version_info[:2] != (3, 10):
        die(f"se esperaba Python 3.10 (python310.dll); este es {sys.version.split()[0]}")
    revisar_site_packages(site)
    llama = a.llama_bin or next((c for c in LLAMA_CANDIDATES if os.path.isfile(os.path.join(c, "llama-server.exe"))), "")
    if not llama or not os.path.isfile(os.path.join(llama, "llama-server.exe")):
        die("no se encontró llama-server.exe; usá --llama-bin")
    ucrt = None if a.sin_ucrt else find_ucrt()
    if not a.sin_ucrt and not ucrt:
        say("  AVISO: no se encontró el UCRT redistribuible del Windows SDK; el paquete depende del UCRT del equipo destino.")
    version = leer_version()

    say(f"Versión: {version}")
    say(f"Python origen: {py} ({sys.version.split()[0]}) + paquetes de {site}")
    say(f"Destino: {out}")
    os.makedirs(out, exist_ok=True)
    t0 = time.time()
    replace_managed(os.path.join(out, "app"), fill_app)
    replace_managed(os.path.join(out, "runtime"), make_runtime_filler(py, site, ucrt))
    replace_managed(os.path.join(out, "bin"), make_bin_filler(llama, ucrt))

    escribir_raiz(out, version)
    say(f"Armado en {time.time() - t0:.0f} s")

    total = sum(os.path.getsize(os.path.join(dp, f)) for dp, _d, fs in os.walk(out) for f in fs)
    say(f"Tamaño total: {total / 1024**3:.2f} GB")
    verificar(out, a.sin_verificar)


def escribir_raiz(out, version):
    """Lo que va en la raíz de la carpeta (fuera de app\\, runtime\\ y bin\\). data\\ y Models\\ solo se crean."""
    for d in ("data", "Models"):
        os.makedirs(os.path.join(out, d), exist_ok=True)
    readme = os.path.join(out, "Models", "LEEME.txt")
    if not os.path.exists(readme):
        write_text(readme, MODELS_README)
    write_text(os.path.join(out, "portable.flag"),
               "Presente = el programa guarda todo en data\\ de esta carpeta y usa runtime\\ y bin\\.\r\n")
    write_text(os.path.join(out, "Diagnostico.bat"), DIAGNOSTICO)
    write_text(os.path.join(out, "Actualizar.bat"), ACTUALIZAR)
    write_text(os.path.join(out, "LEEME.txt"), LEEME.replace("\n", "\r\n"))
    write_text(os.path.join(out, "VERSION.txt"),
               f"Consultor Academico {version}\r\nArmado: {time.strftime('%Y-%m-%d %H:%M')}\r\n")
    build_launcher(out)


def verificar(out, sin_verificar):
    if sin_verificar:
        return
    say("Verificando (diagnóstico con PATH reducido y -I)...")
    # Sin el PATH del equipo (ni Python, ni CUDA, ni llama-server instalados), pero con las variables de usuario que
    # Windows siempre define: sin USERNAME, torch no puede calcular su caché (getpass.getuser) y transformers lo
    # informa como "Could not import module 'PreTrainedModel'", una falla que en uso real no ocurre.
    root = os.environ.get("SystemRoot", r"C:\Windows")
    env = {k: os.environ[k] for k in ("USERNAME", "USERPROFILE", "USERDOMAIN", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP",
                                      "SystemDrive", "ComputerName") if k in os.environ}
    env.update({"SystemRoot": root, "PATH": os.path.join(root, "System32")})
    r = subprocess.run([os.path.join(out, "runtime", "python.exe"), "-I", os.path.join(out, "app", "diagnostico.py")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, cwd=out, timeout=600)
    print(r.stdout)
    if r.stderr.strip():
        print(r.stderr)
    say(f"diagnóstico terminó con código {r.returncode}")
    sys.exit(r.returncode)


if __name__ == "__main__":
    main()
