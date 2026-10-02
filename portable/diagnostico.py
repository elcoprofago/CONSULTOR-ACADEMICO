"""Diagnóstico de la carpeta portable del consultor académico.

Va en app\\ y se corre con el Python propio:
    runtime\\python.exe -I app\\diagnostico.py [--completo]

Revisa que runtime\\ sea autosuficiente (ninguna ruta de Python fuera de la
carpeta), que estén los paquetes del backend, app\\, bin\\ (llama-server y
pdftotext, este último convirtiendo un PDF de prueba) y dónde están los
modelos. --completo además carga el modelo de embeddings y embebe una frase.

Cada línea dice OK, AVISO o FALLA. Termina con código 1 si hubo alguna FALLA.
El informe queda también en data\\diagnostico.txt.
"""

import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

APP = Path(__file__).resolve().parent
RAIZ = APP.parent
UNIDAD = Path(RAIZ.anchor)

MODELO_LLM = "Qwen3.5-9B-Q4_K_M.gguf"
MODELO_EMBEDDINGS = "multilingual-e5-large"

PAQUETES = ("fastapi", "uvicorn", "pydantic", "requests", "numpy", "faiss", "fitz", "torch",
            "sentence_transformers", "transformers", "tqdm")
ARCHIVOS_APP = ("CONSULTOR-ACADEMICO-GUI.exe", "VERSION", "actualizar.py", "backend/main.py", "scripts/config.py", "scripts/consultar.py",
                "scripts/ingesta.py", "scripts/indexado.py", "scripts/responder_api.py",
                "scripts/extraer_texto.py", "scripts/chunking.py", "scripts/texto_io.py")

lineas = []
fallas = 0


def informar(estado, texto):
    global fallas
    if estado == "FALLA":
        fallas += 1
    linea = f"{estado:5}  {texto}"
    lineas.append(linea)
    print(linea, flush=True)


def dentro(p, base):
    try:
        Path(p).resolve().relative_to(base.resolve())
        return True
    except ValueError:
        return False


def revisar_python():
    informar("OK", f"Python {sys.version.split()[0]} en {sys.executable}")
    runtime = RAIZ / "runtime"
    if not dentro(sys.prefix, runtime):
        informar("FALLA", f"sys.prefix={sys.prefix} está fuera de {runtime}")
    afuera = [p for p in sys.path if p and not dentro(p, RAIZ)]
    if afuera:
        informar("FALLA", f"rutas de Python fuera de la carpeta: {afuera}")
    else:
        informar("OK", "todas las rutas de Python están dentro de la carpeta")


def revisar_paquetes():
    for nombre in PAQUETES:
        try:
            mod = __import__(nombre)
            informar("OK", f"{nombre} {getattr(mod, '__version__', '')}".rstrip())
        except Exception as exc:  # noqa: BLE001 -- se informa cualquier error de import
            informar("FALLA", f"{nombre}: {exc}")
    try:
        import torch
        if torch.cuda.is_available():
            informar("OK", f"GPU: {torch.cuda.get_device_name(0)} (CUDA {torch.version.cuda})")
        else:
            informar("AVISO", "sin GPU CUDA: los embeddings se calculan en la CPU (más lento al indexar)")
    except Exception:  # noqa: BLE001 -- ya informado arriba
        pass


def revisar_app():
    faltan = [a for a in ARCHIVOS_APP if not (APP / a).is_file()]
    if faltan:
        informar("FALLA", f"faltan en app\\: {', '.join(faltan)}")
    else:
        version = (APP / "VERSION").read_text(encoding="utf-8").strip()
        informar("OK", f"app\\ completa ({len(ARCHIVOS_APP)} archivos clave), versión {version}")


def revisar_bin():
    llama = RAIZ / "bin" / "llama-server.exe"
    if not llama.is_file():
        informar("FALLA", f"no está {llama}")
    else:
        r = subprocess.run([str(llama), "--version"], capture_output=True, text=True, errors="replace", timeout=60)
        salida = (r.stdout + r.stderr).strip().splitlines()
        version = next((s for s in salida if s.startswith("version")), salida[-1] if salida else "")
        informar("OK" if r.returncode == 0 else "FALLA", f"llama-server: {version} (código {r.returncode})")

    pdftotext = RAIZ / "bin" / "pdftotext.exe"
    if not pdftotext.is_file():
        informar("FALLA", f"no está {pdftotext}")
        return
    try:
        import fitz
        with tempfile.TemporaryDirectory() as tmp:
            pdf, txt = Path(tmp) / "prueba.pdf", Path(tmp) / "prueba.txt"
            doc = fitz.open()
            doc.new_page().insert_text((72, 72), "marcador diagnostico 4711")
            doc.save(str(pdf))
            doc.close()
            r = subprocess.run([str(pdftotext), "-enc", "UTF-8", str(pdf), str(txt)],
                               capture_output=True, text=True, errors="replace", timeout=60)
            leido = txt.read_text(encoding="utf-8", errors="replace") if txt.is_file() else ""
            if r.returncode == 0 and "marcador diagnostico 4711" in leido:
                informar("OK", "pdftotext convierte un PDF de prueba")
            else:
                informar("FALLA", f"pdftotext (código {r.returncode}): {(r.stderr or leido).strip()[:200]}")
    except Exception as exc:  # noqa: BLE001
        informar("FALLA", f"pdftotext: {exc}")


def carpetas_modelos():
    return [RAIZ / "Models", UNIDAD / "MODELS"]


def buscar(nombre, es_carpeta):
    for base in carpetas_modelos():
        if not base.is_dir():
            continue
        for raiz, dirs, archivos in os.walk(base):
            if len(Path(raiz).relative_to(base).parts) > 3:
                dirs.clear()
                continue
            for n in (dirs if es_carpeta else archivos):
                if n.lower() == nombre.lower():
                    p = Path(raiz) / n
                    if not es_carpeta or ((p / "modules.json").is_file() and (p / "config.json").is_file()):
                        return p
    return None


def revisar_modelos(completo):
    donde = ", ".join(str(c) for c in carpetas_modelos())
    llm = buscar(MODELO_LLM, es_carpeta=False)
    if llm:
        informar("OK", f"{MODELO_LLM}: {llm} ({llm.stat().st_size / 1024**3:.2f} GB)")
    else:
        informar("AVISO", f"no está {MODELO_LLM} en {donde} (o en la carpeta elegida en Configuración)")
    emb = buscar(MODELO_EMBEDDINGS, es_carpeta=True)
    if emb:
        informar("OK", f"{MODELO_EMBEDDINGS}: {emb}")
    else:
        informar("AVISO", f"no está la carpeta {MODELO_EMBEDDINGS} en {donde} (o en la carpeta elegida en Configuración)")
    if completo and emb:
        try:
            from sentence_transformers import SentenceTransformer
            t0 = time.time()
            modelo = SentenceTransformer(str(emb), device="cpu")
            v = modelo.encode(["query: prueba de diagnóstico"])
            informar("OK", f"embeddings: vector de {v.shape[1]} dimensiones en {time.time() - t0:.1f} s")
        except Exception as exc:  # noqa: BLE001
            informar("FALLA", f"no se pudo cargar {emb}: {exc}")


def revisar_carpetas():
    for nombre in ("data", "BASE"):
        p = RAIZ / nombre if nombre == "data" else UNIDAD / nombre
        informar("OK" if p.is_dir() else "AVISO", f"{p} {'existe' if p.is_dir() else 'no existe'}")


def main():
    # Con -I Python ignora PYTHONIOENCODING: redirigida, la salida saldría en cp1252 y quien la lee espera UTF-8.
    sys.stdout.reconfigure(encoding="utf-8")
    completo = "--completo" in sys.argv
    os.environ.setdefault("HF_HUB_OFFLINE", "1")
    informar("OK", f"Carpeta: {RAIZ}  ({time.strftime('%Y-%m-%d %H:%M:%S')})")
    revisar_python()
    revisar_paquetes()
    revisar_app()
    revisar_bin()
    revisar_modelos(completo)
    revisar_carpetas()
    informar("OK" if fallas == 0 else "FALLA", f"{fallas} falla(s)")
    data = RAIZ / "data"
    if data.is_dir():
        (data / "diagnostico.txt").write_text("\r\n".join(lineas) + "\r\n", encoding="utf-8")
    sys.exit(1 if fallas else 0)


if __name__ == "__main__":
    main()
