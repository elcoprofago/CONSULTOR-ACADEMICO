# Consultor Académico

Aplicación de escritorio (WPF, .NET 8) para consultar una biblioteca propia de PDF académicos: búsqueda semántica
(embeddings `multilingual-e5-large` + FAISS) y respuestas de un modelo local (`llama-server` con
Qwen3.5-9B), con la ficha bibliográfica de cada documento tomada del catálogo de CitaPDF (solo lectura).

- `*.xaml`, `*.cs`: la ventana.
- `BACKEND/main.py`: API local (FastAPI, puerto 8001) que la ventana lanza al abrir. Usa los scripts de
  `ACADEMICO-PROYECTO/ACADEMICO-SCRIPTS`, un repo aparte.
- `portable/`: la versión portable (carpeta autosuficiente que arranca desde el disco o desde un pendrive).

## Versión portable

```
python portable\build_portable.py --out <carpeta>              arma la carpeta completa
python portable\build_portable.py --out <carpeta> --solo-app   rehace solo app\
```

La carpeta trae su propio Python (`runtime\`), `llama-server` y `pdftotext` (`bin\`), la aplicación (`app\`), su
índice (`data\`) y un lanzador `CONSULTOR-ACADEMICO.exe`. Los modelos se buscan en `Models\` de la carpeta, en
`<unidad>:\MODELS` o en la carpeta elegida en Configuración. `Diagnostico.bat` revisa que esté todo.

## Publicar una versión

1. Subir `<Version>` en `CONSULTOR-ACADEMICO-GUI.csproj` y commitear.
2. `python portable\publicar.py --prueba` (controles y zip, sin publicar) y luego sin `--prueba`.

`publicar.py` se niega si hay cambios sin commitear, si la etiqueta ya existe o si la versión no es mayor que la
última publicada. Crea la etiqueta `v<versión>` y una release con `CONSULTOR-ACADEMICO-app-<versión>.zip` (el
contenido de `app\`). Las copias portables la instalan con el botón **Actualizar** o con `Actualizar.bat`;
`Actualizar.bat <versión>` vuelve a una versión anterior.
