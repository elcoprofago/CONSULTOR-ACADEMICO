"""Actualizador de la carpeta portable: baja una release de GitHub y reemplaza app\\ por esa versión.

Va en app\\ y se corre con el Python propio:
    Actualizar.bat                     la última versión publicada
    Actualizar.bat 1.0.0               una versión dada (por ejemplo, para volver a una anterior)
El botón «Actualizar» de la ventana hace lo mismo: corre una copia de este archivo fuera de app\\ con --esperar-cierre
y se cierra; la copia espera a que el consultor termine de cerrarse (GUI, backend y llama-server), instala y lo
vuelve a abrir. El resultado queda en data\\actualizacion.txt, que la ventana muestra en el log al abrir.

La release trae un solo asset, CONSULTOR-ACADEMICO-app-<versión>.zip, con el contenido de app\\ (lo arma
portable\\publicar.py). Antes de instalar se comprueba que su app\\VERSION diga la misma versión que la etiqueta.

Solo cambia app\\ y VERSION.txt; data\\, Models\\, runtime\\ y bin\\ no se tocan. La app\\ anterior se conserva
mientras dura el reemplazo (si algo falla a mitad, se vuelve a ella) y se borra al terminar bien: en el pendrive no
quedan respaldos. Para volver a una versión anterior: Actualizar.bat <versión>.
"""
import argparse
import ctypes
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import zipfile

REPO = "elcoprofago/CONSULTOR-ACADEMICO"
API = f"https://api.github.com/repos/{REPO}/releases"
MARKER = ".build_portable"
OBLIGATORIOS = ("CONSULTOR-ACADEMICO-GUI.exe", "VERSION", "actualizar.py", "diagnostico.py",
                "backend/main.py", "scripts/config.py")
RESULTADO = "actualizacion.txt"      # en data\: lo lee (y lo borra) la ventana al abrir
ESPERA_CIERRE = 120                  # segundos que se espera a que el consultor termine de cerrarse
SEPARADO = 0x00000008 | 0x00000200   # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP

APP = os.path.dirname(os.path.abspath(__file__))
RAIZ = os.path.dirname(APP)


def nombre_asset(v):
    return f"CONSULTOR-ACADEMICO-app-{v}.zip"


def clave(v):
    """'1.0.10' -> (1, 0, 10), para comparar versiones como números y no como texto."""
    return tuple(int(x) for x in v.strip().lstrip("vV").split("."))


def leer_version(app_dir):
    with open(os.path.join(app_dir, "VERSION"), encoding="utf-8") as f:
        return f.readline().strip()


def escribir_version_txt(raiz, v):
    with open(os.path.join(raiz, "VERSION.txt"), "w", encoding="ascii", newline="") as f:
        f.write(f"Consultor Academico {v}\r\nActualizado: {time.strftime('%Y-%m-%d %H:%M')}\r\n")


def procesos_de_esta_carpeta():
    """PIDs que corren un ejecutable de app\\, runtime\\ o bin\\ de esta carpeta (sin contar este proceso)."""
    k32 = ctypes.windll.kernel32
    psapi = ctypes.windll.psapi
    pids = (ctypes.c_ulong * 4096)()
    usado = ctypes.c_ulong()
    if not psapi.EnumProcesses(ctypes.byref(pids), ctypes.sizeof(pids), ctypes.byref(usado)):
        return []
    bases = tuple(os.path.normcase(os.path.join(RAIZ, d)) + os.sep for d in ("app", "runtime", "bin"))
    propios = []
    for pid in pids[:usado.value // ctypes.sizeof(ctypes.c_ulong)]:
        if pid in (0, os.getpid()):
            continue
        h = k32.OpenProcess(0x1000, False, pid)       # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            continue
        try:
            buf = ctypes.create_unicode_buffer(1024)
            n = ctypes.c_ulong(len(buf))
            if k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)):
                if os.path.normcase(buf.value).startswith(bases):
                    propios.append(pid)
        finally:
            k32.CloseHandle(h)
    return propios


def pedir(url, destino=None):
    req = urllib.request.Request(url, headers={"User-Agent": "CONSULTOR-ACADEMICO-actualizador",
                                               "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(req, timeout=60) as r:
        if destino is None:
            return json.loads(r.read().decode("utf-8"))
        with open(destino, "wb") as f:
            shutil.copyfileobj(r, f)


def buscar_release(version=None):
    """(versión, url del zip de app\\). Sin versión, la última publicada. Las fallas de red se lanzan tal cual."""
    rel = pedir(f"{API}/latest" if version is None else f"{API}/tags/v{version.lstrip('vV')}")
    v = str(rel.get("tag_name", "")).lstrip("vV")
    asset = next((a for a in rel.get("assets", []) if a.get("name") == nombre_asset(v)), None)
    if asset is None:
        raise RuntimeError(f"la release v{v} no trae {nombre_asset(v)}")
    return v, asset["browser_download_url"]


def armar_app_nueva(zip_path, dest, v_esperada):
    """Extrae el zip en dest y comprueba que esté completo y que sea la versión anunciada."""
    with zipfile.ZipFile(zip_path) as z:
        malo = z.testzip()
        if malo:
            raise RuntimeError(f"el zip descargado está dañado ({malo})")
        for nombre in z.namelist():
            destino = os.path.normpath(os.path.join(dest, nombre))
            if not destino.startswith(os.path.normpath(dest) + os.sep):
                raise RuntimeError(f"el zip trae una ruta fuera de app\\: {nombre}")
        z.extractall(dest)
        n = len([i for i in z.infolist() if not i.is_dir()])
    faltan = [o for o in OBLIGATORIOS if not os.path.isfile(os.path.join(dest, o))]
    if faltan:
        raise RuntimeError(f"la release no trae {', '.join(faltan)}: no se instala")
    v_zip = leer_version(dest)
    if clave(v_zip) != clave(v_esperada):
        raise RuntimeError(f"la release se llama v{v_esperada} pero su app\\VERSION dice {v_zip}: no se instala")
    with open(os.path.join(dest, MARKER), "w", encoding="utf-8") as f:
        f.write("Carpeta generada por build_portable.py; se reemplaza entera al rearmar. No pongas nada tuyo acá.\n")
    return n


def preparar(url, v):
    """Descarga el zip y lo deja verificado en app.new\\. No toca app\\."""
    nueva_app = os.path.join(RAIZ, "app.new")
    tmp = tempfile.mkdtemp(prefix="consultor_update_")
    try:
        if os.path.exists(nueva_app):
            shutil.rmtree(nueva_app)
        os.makedirs(nueva_app)
        zip_path = os.path.join(tmp, nombre_asset(v))
        pedir(url, zip_path)
        return armar_app_nueva(zip_path, nueva_app, v)
    except Exception:
        shutil.rmtree(nueva_app, ignore_errors=True)
        raise
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def instalar(nueva):
    """app.new\\ pasa a ser app\\. La anterior se aparta mientras tanto (si algo falla, se vuelve a ella) y se borra
    al terminar bien. El consultor tiene que estar cerrado."""
    nueva_app = os.path.join(RAIZ, "app.new")
    if not os.path.isfile(os.path.join(nueva_app, MARKER)):
        raise RuntimeError("no hay una versión descargada y verificada en app.new")
    anterior = os.path.join(RAIZ, "app.anterior")
    if os.path.exists(anterior):
        shutil.rmtree(anterior)
    os.rename(APP, anterior)            # si esto falla (algo tiene abierto app\), no se cambió nada
    try:
        os.rename(nueva_app, APP)
        escribir_version_txt(RAIZ, nueva)
    except OSError:
        if os.path.exists(APP):
            shutil.rmtree(APP)
        os.rename(anterior, APP)
        raise
    shutil.rmtree(anterior, ignore_errors=True)


def escribir_resultado(texto):
    d = os.path.join(RAIZ, "data")
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, RESULTADO), "w", encoding="utf-8") as f:
        f.write(texto + "\n")


def esperar_cierre(espera=ESPERA_CIERRE):
    fin = time.time() + espera
    corriendo = procesos_de_esta_carpeta()
    while corriendo and time.time() < fin:
        time.sleep(0.5)
        corriendo = procesos_de_esta_carpeta()
    return corriendo


def actualizar(pedida, forzar):
    """Hace todo y devuelve (ok, mensaje). Nunca deja app\\ a medio cambiar."""
    actual = leer_version(APP)
    try:
        nueva, url = buscar_release(pedida)
    except Exception as e:  # noqa: BLE001 -- sin red, GitHub caído, versión inexistente: se informa, no se toca nada
        return False, f"No se pudo consultar GitHub ({e}). Sigue la versión {actual}, sin cambios."
    if pedida is None and not forzar and clave(nueva) <= clave(actual):
        return True, f"Ya está la última versión ({actual}). No hay nada que hacer."
    if clave(nueva) == clave(actual) and not forzar:
        return True, f"La versión {actual} ya es la instalada. No hay nada que hacer."
    corriendo = procesos_de_esta_carpeta()
    if corriendo:
        return False, (f"El consultor está abierto desde esta carpeta (proceso {', '.join(map(str, corriendo))}). "
                       f"Cerralo y volvé a correr Actualizar.bat, o usá el botón «Actualizar» de la ventana.")
    try:
        print(f"Descargando la versión {nueva}...", flush=True)
        n = preparar(url, nueva)
        instalar(nueva)
    except Exception as e:  # noqa: BLE001 -- instalar() ya volvió a la anterior
        shutil.rmtree(os.path.join(RAIZ, "app.new"), ignore_errors=True)   # en el pendrive no quedan restos
        return False, f"FALLÓ la actualización a {nueva}: {e}. Sigue la versión {actual}, sin cambios."
    return True, f"Actualizado {actual} -> {nueva} ({n} archivos)."


def main():
    global RAIZ, APP
    ap = argparse.ArgumentParser(description="Actualiza app\\ de la carpeta portable desde GitHub.")
    ap.add_argument("version", nargs="?", help="versión a instalar (por defecto, la última publicada)")
    ap.add_argument("--forzar", action="store_true", help="reinstalar aunque sea la misma versión")
    ap.add_argument("--esperar-cierre", action="store_true", help="(lo usa el botón de la ventana)")
    ap.add_argument("--raiz", help="(lo usa el botón de la ventana: corre desde una copia fuera de app\\)")
    a = ap.parse_args()
    if a.raiz:
        RAIZ = os.path.abspath(a.raiz)
        APP = os.path.join(RAIZ, "app")
    desde_ventana, forzar, pedida = a.esperar_cierre, a.forzar, a.version

    print("Consultor Academico - actualizador\n")
    if not os.path.isfile(os.path.join(RAIZ, "portable.flag")):
        print("Esta no es una carpeta portable (falta portable.flag). No se toca nada.")
        return 1
    print(f"Versión instalada: {leer_version(APP)}")
    if desde_ventana:
        print("Esperando a que el consultor termine de cerrarse...", flush=True)
        corriendo = esperar_cierre()
        if corriendo:
            ok, msg = False, (f"El consultor seguía abierto (proceso {', '.join(map(str, corriendo))}) después de "
                              f"{ESPERA_CIERRE} s. Sigue la versión {leer_version(APP)}, sin cambios.")
        else:
            ok, msg = actualizar(pedida, forzar)
        escribir_resultado(("OK " if ok else "FALLÓ ") + msg)
        print("\n" + msg, flush=True)
        exe = os.path.join(RAIZ, "CONSULTOR-ACADEMICO.exe")
        if os.path.isfile(exe):
            subprocess.Popen([exe], cwd=RAIZ, close_fds=True, creationflags=SEPARADO)
        if not ok:
            input("\nEnter para cerrar esta ventana...")
        else:
            time.sleep(4)
        # Esta copia la dejó la ventana en una carpeta temporal propia: se la lleva al terminar.
        aqui = os.path.dirname(os.path.abspath(__file__))
        if os.path.basename(aqui).startswith("consultor_update_"):
            shutil.rmtree(aqui, ignore_errors=True)
        return 0 if ok else 1
    ok, msg = actualizar(pedida, forzar)
    print("\n" + msg)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
