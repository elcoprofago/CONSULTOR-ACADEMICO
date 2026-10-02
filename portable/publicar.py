"""Publica una versión del consultor en GitHub: etiqueta v<versión> y una release con app\\ en un zip, que es lo que
baja el botón «Actualizar» de las copias portables (ver actualizar.py).

Uso, con el Python del venv (el mismo que build_portable.py):
    ..\\..\\CSJN-PROYECTO\\CSJN-SCRIPTS\\venv\\Scripts\\python.exe portable\\publicar.py [--prueba]

La versión es la <Version> de CONSULTOR-ACADEMICO-GUI.csproj. Se niega a publicar si:
- no se está en la rama master o hay cambios sin commitear (en este repo o en ACADEMICO-PROYECTO, de donde salen
  los scripts que van en app\\scripts);
- la etiqueta v<versión> ya existe (acá o en GitHub);
- la versión no es mayor que la última publicada;
- el zip armado no trae los archivos que actualizar.py exige o su app\\VERSION no coincide con la etiqueta.
--prueba hace todos los controles y arma y verifica el zip, pero no etiqueta, no sube ni publica nada.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import actualizar  # noqa: E402 -- qué exige la release (OBLIGATORIOS, nombre del asset) se decide en un solo lugar
import build_portable as bp  # noqa: E402

RAMA = "master"
ACADEMICO = os.path.dirname(bp.SCRIPTS)


def die(msg):
    print("NO SE PUBLICA: " + msg, file=sys.stderr, flush=True)
    sys.exit(1)


def run(cmd, cwd=bp.REPO, check=True):
    r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if check and r.returncode != 0:
        die(f"{' '.join(cmd)} falló (código {r.returncode}):\n{(r.stdout + r.stderr).strip()}")
    return r


def controles(v):
    rama = run(["git", "rev-parse", "--abbrev-ref", "HEAD"]).stdout.strip()
    if rama != RAMA:
        die(f"se está en la rama {rama}, no en {RAMA}")
    for repo in (bp.REPO, ACADEMICO):
        sucio = run(["git", "status", "--porcelain", "--untracked-files=no"], cwd=repo).stdout.strip()
        if sucio:
            die(f"hay cambios sin commitear en {repo}:\n{sucio}")
    tag = f"v{v}"
    if run(["git", "tag", "-l", tag]).stdout.strip():
        die(f"la etiqueta {tag} ya existe en este repo: subí <Version> en el .csproj")
    if run(["git", "ls-remote", "--tags", "origin", f"refs/tags/{tag}"]).stdout.strip():
        die(f"la etiqueta {tag} ya existe en GitHub: subí <Version> en el .csproj")
    r = run(["gh", "release", "view", "--repo", actualizar.REPO, "--json", "tagName"], check=False)
    if r.returncode == 0:
        ultima = json.loads(r.stdout)["tagName"].lstrip("vV")
        if actualizar.clave(v) <= actualizar.clave(ultima):
            die(f"la versión {v} no es mayor que la última publicada ({ultima})")
        print(f"Última publicada: {ultima}")
    elif "release not found" in (r.stdout + r.stderr).lower():
        print("Todavía no hay releases publicadas: esta es la primera.")
    else:
        die(f"no se pudo consultar la última release:\n{(r.stdout + r.stderr).strip()}")


def armar_zip(v, carpeta):
    app = os.path.join(carpeta, "app")
    os.makedirs(app)
    bp.fill_app(app)
    zip_path = os.path.join(carpeta, actualizar.nombre_asset(v))
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for dp, _d, fs in os.walk(app):
            for f in fs:
                p = os.path.join(dp, f)
                z.write(p, os.path.relpath(p, app).replace(os.sep, "/"))
    return zip_path


def verificar_zip(zip_path, v):
    """El mismo control que hará actualizar.py antes de instalar, hecho antes de publicar."""
    with tempfile.TemporaryDirectory() as tmp:
        try:
            n = actualizar.armar_app_nueva(zip_path, tmp, v)
        except Exception as e:  # noqa: BLE001
            die(f"el zip armado no pasa el control de actualizar.py: {e}")
    print(f"Zip verificado: {n} archivos, app\\VERSION = {v}, {os.path.getsize(zip_path) / 1024**2:.1f} MB")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--prueba", action="store_true", help="controles y zip, sin etiquetar ni publicar nada")
    a = ap.parse_args()

    v = bp.leer_version()
    tag = f"v{v}"
    print(f"Versión a publicar: {v}")
    controles(v)
    commit_scripts = run(["git", "rev-parse", "--short", "HEAD"], cwd=ACADEMICO).stdout.strip()

    carpeta = tempfile.mkdtemp(prefix="consultor_publicar_")
    zip_path = armar_zip(v, carpeta)
    verificar_zip(zip_path, v)
    if a.prueba:
        print(f"--prueba: no se publica nada. El zip quedó en {zip_path} para revisarlo (borralo después).")
        return

    try:
        run(["git", "push", "origin", RAMA])
        run(["git", "tag", tag])
        run(["git", "push", "origin", tag])
        notas = (f"Consultor Académico {v}.\n\n"
                 f"El zip es la carpeta app\\ de la versión portable (lo instala el botón «Actualizar» o "
                 f"Actualizar.bat). Los scripts de app\\scripts salen de ACADEMICO-PROYECTO, commit {commit_scripts}.")
        run(["gh", "release", "create", tag, zip_path, "--repo", actualizar.REPO, "--verify-tag",
             "--title", f"Consultor Académico {v}", "--notes", notas])
    finally:
        shutil.rmtree(carpeta, ignore_errors=True)

    # Lo publicado, visto como lo ve el actualizador.
    publicada, url = actualizar.buscar_release()
    if publicada != v:
        die(f"GitHub informa como última la {publicada}, no la {v}")
    print(f"Publicada {tag}: {url}")


if __name__ == "__main__":
    main()
