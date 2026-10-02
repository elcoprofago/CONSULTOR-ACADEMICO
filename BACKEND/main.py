"""Servicio HTTP local (FastAPI) que envuelve responder_api()/ingesta.py de
ACADEMICO-SCRIPTS (ver plan de implementación, Requisitos 13-16).

No reescribe el pipeline RAG: importa y llama a responder_api()/
listar_documentos()/ingesta.ingestar_documento(), código que vive en
ACADEMICO-PROYECTO/ACADEMICO-SCRIPTS y se reutiliza sin cambios. Este archivo
es la única pieza de código Python nueva que vive en CONSULTOR-ACADEMICO-GUI.

A diferencia de CONSULTOR-GUI/BACKEND/main.py (que carga dos colecciones,
CSJN y PGN, con un truco de import dinámico por alias + purga de
sys.modules para que convivan pese a compartir nombres de módulo como
"config"/"consultar"), acá hay una sola colección: import normal, sin alias.

Uso (con el venv compartido de CSJN-PROYECTO, que ya tiene faiss-cpu,
sentence-transformers y torch instalados):

    ..\..\CSJN-PROYECTO\CSJN-SCRIPTS\venv\Scripts\python.exe -m uvicorn main:app --host 127.0.0.1 --port 8001
"""

import os

# Ver CONSULTOR-GUI/BACKEND/main.py para el incidente que motivó esto: sin
# HF_HUB_OFFLINE, SentenceTransformer(...) sale a validar el modelo contra
# el Hub en CADA arranque aunque ya esté cacheado localmente -- y si esa
# llamada se cuelga (HF Hub lento/rate-limiteando pedidos sin token), el
# import de main.py nunca termina, uvicorn nunca llega a escuchar el puerto,
# y la GUI ve "backend no respondió a tiempo" sin actividad visible.
os.environ.setdefault("HF_HUB_OFFLINE", "1")

import sys
from pathlib import Path
from typing import List, Optional

from fastapi import FastAPI, Form, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

# BACKEND -> CONSULTOR-ACADEMICO-GUI -> C.S.J.N: sin letra de unidad fija.
ACADEMICO_SCRIPTS_DIR = Path(__file__).resolve().parents[2] / "ACADEMICO-PROYECTO" / "ACADEMICO-SCRIPTS"
sys.path.insert(0, str(ACADEMICO_SCRIPTS_DIR))

print("Cargando biblioteca académica (embeddings + índice FAISS)...", flush=True)
import ingesta  # noqa: E402
import responder_api as api  # noqa: E402

# Contrato con ACADEMICO-SCRIPTS (otro repo): este backend manda la ruta del
# PDF, no sus bytes, registra rutas nuevas con actualizar_ruta() y vínculos
# con fichas de CitaPDF con vincular_ficha(). Con un ingesta.py anterior
# fallaría recién al usar esas funciones, con un error 500 sin explicación.
_faltan = [n for n in ("actualizar_ruta", "vincular_ficha", "FichaInvalidaError") if not hasattr(ingesta, n)]
if _faltan:
    raise SystemExit(
        f"ACADEMICO-SCRIPTS/ingesta.py es anterior a esta versión del consultor (falta {', '.join(_faltan)}). "
        "Actualizá ACADEMICO-PROYECTO junto con CONSULTOR-ACADEMICO-GUI."
    )

print("Backend listo.", flush=True)


app = FastAPI(title="CONSULTOR-ACADEMICO-GUI backend")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


class ConsultaRequest(BaseModel):
    pregunta: str
    filtro: Optional[List[str]] = None  # documento_ids
    k: Optional[int] = None


class FragmentoResponse(BaseModel):
    documento_id: str
    titulo: str
    autor: str
    anio: str
    pagina_inicio: int
    pagina_fin: int
    texto: str
    cita: str


class ConsultaResponse(BaseModel):
    rama: str
    respuesta: Optional[str] = None
    mensaje: Optional[str] = None
    fragmentos: List[FragmentoResponse]
    error_llm: Optional[str] = None
    # Citas de los fragmentos (a lo sumo K_MAX_CONTEXTO_LLM) que realmente
    # se le mandaron al LLM como contexto -- puede ser un subconjunto chico
    # de "fragmentos", que la GUI pagina/muestra sin ese límite.
    citas_contexto: Optional[List[str]] = None


class DocumentoResponse(BaseModel):
    documento_id: str
    titulo: str
    autor: str
    anio: str
    fuente_editorial: str
    n_chunks: int
    # Ruta del PDF original (el consultor no guarda copia). None en los
    # documentos anteriores a ese cambio que todavía no se ubicaron; para
    # ésos, copia_interna es la copia vieja de ACADEMICO-PDF si existe.
    ruta_archivo: Optional[str] = None
    origen_url: Optional[str] = None
    hash_sha256: str = ""
    copia_interna: Optional[str] = None
    # Vínculo elegido a mano con un registro del catálogo de CitaPDF (su
    # HashSha256), para cuando el PDF no es el mismo archivo catalogado.
    ficha_citapdf: Optional[str] = None


class RutaRequest(BaseModel):
    ruta_archivo: str


class FichaRequest(BaseModel):
    ficha_citapdf: Optional[str] = None  # None quita el vínculo


class IngestaResponse(BaseModel):
    documento_id: str
    titulo: str
    autor: str
    anio: str
    n_paginas: int
    n_chunks: int
    advertencias: List[str]
    ruta_archivo: str


@app.get("/health")
def health():
    return {"status": "ok"}


@app.get("/documentos", response_model=List[DocumentoResponse])
def documentos():
    return api.listar_documentos()


@app.post("/consultar", response_model=ConsultaResponse)
def consultar(req: ConsultaRequest):
    if not req.pregunta or not req.pregunta.strip():
        raise HTTPException(400, "La pregunta no puede estar vacía.")
    return api.responder_api(req.pregunta, filtro_documentos=req.filtro, k=req.k)


@app.post("/ingestar", response_model=IngestaResponse)
def ingestar(
    titulo: str = Form(...),
    autor: str = Form(""),
    anio: str = Form(""),
    fuente_editorial: str = Form(""),
    ruta_archivo: Optional[str] = Form(None),
    origen_url: Optional[str] = Form(None),
    carpeta_descargas: Optional[str] = Form(None),
    ficha_citapdf: Optional[str] = Form(None),
):
    """El PDF ya no viaja en la request: la GUI manda la ruta del archivo
    local (que se indexa desde ahí, sin copiarlo) o la URL más la carpeta
    donde guardar la descarga."""
    if not titulo or not titulo.strip():
        raise HTTPException(400, "El título es obligatorio.")

    ruta = ruta_archivo.strip() if ruta_archivo and ruta_archivo.strip() else None
    url = origen_url.strip() if origen_url and origen_url.strip() else None
    if bool(ruta) == bool(url):
        raise HTTPException(400, "Se requiere exactamente uno de archivo o URL de descarga directa.")
    carpeta = carpeta_descargas.strip() if carpeta_descargas and carpeta_descargas.strip() else None

    try:
        return ingesta.ingestar_documento(
            titulo=titulo.strip(),
            ruta_archivo=ruta,
            origen_url=url,
            carpeta_descargas=carpeta,
            autor=autor.strip(),
            anio=anio.strip(),
            fuente_editorial=fuente_editorial.strip(),
            ficha_citapdf=ficha_citapdf,
        )
    except ingesta.DocumentoDuplicadoError as exc:
        raise HTTPException(
            409, {"mensaje": str(exc), "documento_id_existente": exc.documento_id_existente}
        ) from exc
    except (ingesta.PdfInvalidoError, ingesta.TextoNoExtraibleError, ingesta.DescargaFallidaError,
            ingesta.ArchivoNoEncontradoError, ingesta.CarpetaDescargasError,
            ingesta.FichaInvalidaError) as exc:
        raise HTTPException(400, str(exc)) from exc


@app.post("/documentos/{documento_id}/ruta", response_model=DocumentoResponse)
def actualizar_ruta(documento_id: str, req: RutaRequest):
    """Registra dónde está ahora el PDF de un documento (movido, renombrado,
    o anterior al cambio que dejó de copiar PDFs). Sólo acepta el mismo
    archivo (mismo SHA-256) que el indexado."""
    try:
        ingesta.actualizar_ruta(documento_id, req.ruta_archivo)
    except ingesta.DocumentoInexistenteError as exc:
        raise HTTPException(404, str(exc)) from exc
    except ingesta.DocumentoDuplicadoError as exc:
        raise HTTPException(
            409, {"mensaje": f"Ese archivo es otro documento de la biblioteca: {exc.documento_id_existente}.",
                  "documento_id_existente": exc.documento_id_existente}
        ) from exc
    except (ingesta.ArchivoNoEncontradoError, ingesta.HashDistintoError) as exc:
        raise HTTPException(400, str(exc)) from exc
    return next(d for d in api.listar_documentos() if d["documento_id"] == documento_id)


@app.post("/documentos/{documento_id}/ficha", response_model=DocumentoResponse)
def vincular_ficha(documento_id: str, req: FichaRequest):
    """Vincula el documento con un registro del catálogo de CitaPDF (o quita
    el vínculo con ficha_citapdf = null). El catálogo lo lee la GUI."""
    try:
        ingesta.vincular_ficha(documento_id, req.ficha_citapdf)
    except ingesta.DocumentoInexistenteError as exc:
        raise HTTPException(404, str(exc)) from exc
    except ingesta.FichaInvalidaError as exc:
        raise HTTPException(400, str(exc)) from exc
    return next(d for d in api.listar_documentos() if d["documento_id"] == documento_id)
