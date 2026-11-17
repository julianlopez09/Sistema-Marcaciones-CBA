# Reproducir la revisión local

La revisión de 17 de noviembre registra 61 requisitos del documento y cinco criterios de entrega. El resultado es **pendiente de actualización integral**. No es una release, un instalador ni una prueba de aceptación del programa.

`RESULTADO_REVISION_ESTATICA_20261117.json` contiene hashes de quince archivos de código y once observaciones positivas sobre el candidato inspeccionado. No incluye el documento, los archivos de marcaciones, identidades, salarios ni bases de datos.

Para reproducir desde PowerShell, con las rutas locales correspondientes:

```powershell
.\tools\Inspect-CbaCandidate.ps1 `
  -ProjectRoot '<raíz de SistemaMarcacionesCBA>' `
  -DirectricesPath '<documento original recibido.docx>' `
  -OutputPath '<carpeta externa existente>\revision-nueva.json'
```

El archivo de salida debe ser nuevo y estar fuera de la aplicación. El script lee exclusivamente el documento y los quince archivos enumerados, comprueba que no cambiaron durante la lectura y escribe únicamente ese informe. No ejecuta la aplicación, Python, .NET, Excel, compiladores o pruebas del producto. No instala dependencias ni cambia configuraciones del sistema.

Se verificó contra la copia local disponible: 62 párrafos no vacíos, 15 fuentes leídas y 11 observaciones encontradas. Esto no significa 61 requisitos superados. El informe distingue expresamente `FunctionalAcceptance=NOT_EXECUTED` y `FinalDelivery=PENDING`.

Los hashes permiten relacionar la revisión con los archivos concretos. Una coincidencia de texto únicamente identifica una condición del código: no demuestra su comportamiento en ejecución. La matriz de conformidad establece los casos que siguen pendientes.

El código de SR3 y sus dependencias de compilación no forman parte todavía de este repositorio. Para una compilación reproducible hace falta incorporar y sanear ese código; no deben copiarse sin selección las carpetas de evidencias, los entornos de prueba ni los datos institucionales.
