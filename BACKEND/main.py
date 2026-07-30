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

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

ACADEMICO_SCRIPTS_DIR = Path(r"E:\INFO\DERECHO\Jurisprudencia\C.S.J.N\ACADEMICO-PROYECTO\ACADEMICO-SCRIPTS")
sys.path.insert(0, str(ACADEMICO_SCRIPTS_DIR))

print("Cargando biblioteca académica (embeddings + índice FAISS)...", flush=True)
import ingesta  # noqa: E402
import responder_api as api  # noqa: E402

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


class IngestaResponse(BaseModel):
    documento_id: str
    titulo: str
    autor: str
    anio: str
    n_paginas: int
    n_chunks: int
    advertencias: List[str]


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
    origen_url: Optional[str] = Form(None),
    archivo: Optional[UploadFile] = File(None),
):
    if not titulo or not titulo.strip():
        raise HTTPException(400, "El título es obligatorio.")

    url = origen_url.strip() if origen_url and origen_url.strip() else None
    if bool(archivo) == bool(url):
        raise HTTPException(400, "Se requiere exactamente uno de archivo o URL de descarga directa.")

    archivo_bytes = archivo.file.read() if archivo else None

    try:
        return ingesta.ingestar_documento(
            titulo=titulo.strip(),
            archivo_bytes=archivo_bytes,
            origen_url=url,
            autor=autor.strip(),
            anio=anio.strip(),
            fuente_editorial=fuente_editorial.strip(),
        )
    except ingesta.DocumentoDuplicadoError as exc:
        raise HTTPException(
            409, {"mensaje": str(exc), "documento_id_existente": exc.documento_id_existente}
        ) from exc
    except (ingesta.PdfInvalidoError, ingesta.TextoNoExtraibleError, ingesta.DescargaFallidaError) as exc:
        raise HTTPException(400, str(exc)) from exc
