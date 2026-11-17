# Revisión de las directrices del Sistema de Marcaciones CBA

Fecha: 17 de noviembre de 2026. Estado: **actualización integral pendiente**.

El candidato SR3 inspeccionado sigue siendo una aplicación de pruebas con entradas admitidas previamente. No constituye una actualización funcional para trabajo institucional. La corrección de exportación de Excel y la apertura de una matriz de ejemplo no acreditan importación libre, cierre mensual, memorandos ni las tres poblaciones con datos reales.

Esta revisión identifica requisitos de todo el documento y los contrasta con las superficies de código indicadas. Es una revisión estática dirigida: no certifica cada motor, no repite pruebas históricas y no declara funciones ausentes en todo el proyecto por una búsqueda sin coincidencias. **No se ha terminado la implementación, la compilación ni la entrega solicitada.**

## Documento de referencia

Documento recibido: `programa CBA directrices.docx`.

SHA-256: `9B7B3605E9CB76D6E1F2F7D142E386E33D01BD61D3DA9AF1FB7D30CE248B307A`.

Las referencias P1 a P62 corresponden a los párrafos no vacíos de `word/document.xml`, en orden de lectura. No son números de página. La extracción conserva todos los textos de cada párrafo, incluidos los situados en tablas. No se publica el documento ni las fuentes institucionales en este repositorio público.

## Hallazgos que impiden llamar final a SR3

1. `ReleaseSession.ValidateConfiguration` exige `TestFixtureOnly=true`. `ReleaseSession.Open` exige `NativeFixtureSandbox` e identidades que empiecen por `SRV-QA-`. Cambiar estos controles a falso o eliminar validaciones no crea una ruta productiva segura.
2. `IntegratedProgram.Main` exige un catálogo de sandbox y su hash mediante variables del entorno. No existe en ese punto un inicio normal con creación o selección libre de un trabajo mensual.
3. `IntegratedWindow` conecta el botón de importar con `ReimportExactQaBiometrics`: vuelve a leer una fuente preadmitida. `ExecutePinnedInputs` recorre entradas predefinidas. Una etiqueta más sencilla no cambia estos comportamientos.
4. La interfaz compartida `InstitutionalShell` ofrece Inicio, Personal, Marcaciones, Documentos, Novedades, Revisión, Exportación y Estado técnico. No coincide con el árbol mensual y los módulos independientes del documento.
5. Los proyectos del candidato referencian ensamblados congelados, ubicaciones absolutas y compilaciones dentro de carpetas de evidencias. La rama remota `main`, en el commit revisado `4e5081f380d83dc59e9db48334102c7fc8c3415d`, contiene 35 archivos: README, exclusiones y metadatos históricos de releases. No contiene el código de SR3. El código local y ese repositorio no son una entrega reproducible equivalente.
6. La paleta del código legado `rules.py` conserva rojo y verde intensos; el permiso médico usa el mismo naranja que vacaciones, y feriados usa amarillo. Esto difiere del documento. `CatalogoEstilos.cs` deriva del catálogo congelado y rechaza categorías nuevas: hay que versionar la nueva política y conectar sus consumidores, preservando salidas históricas.
7. `rules.py` distingue cuatro marcas para Administrativos y dos para Operativos administrativos. Las nuevas directrices agrupan ambos con almuerzo y cuatro registros. Hay que revisar y cambiar la estrategia completa, no únicamente esta constante: el port `Phase4Candidate` mantiene un consumidor específico de Operativo administrativo.
8. `DisciplinePlan` se identifica como vista previa y conserva `MULTI_REASON_PRESENTATION_NOT_INSTITUTIONALLY_APPROVED`. No acredita una matriz disciplinaria final con todos los detalles solicitados.
9. En las rutas de interfaz SR3 inspeccionadas no se encontró generación automática de memorandos Word enlazada con el conjunto final de inconsistencias. Los módulos de documentos del legado revisados cubren Licencias y Vacaciones. El formato institucional del memorando no estuvo disponible en esta sesión.

## Superficies inspeccionadas

Todas las rutas siguientes son relativas a la raíz local `SistemaMarcacionesCBA`; el script de revisión genera sus hashes en un archivo local separado.

| Clave | Ruta y alcance |
|---|---|
| S1 | `MIGRACION/EVIDENCIAS/F13_SR3_AUTONOMOUS_EXPORT_FIX_RESOURCE_RECOVERY_AND_FINAL_QA_01/DEV/CORE_02/ReleaseSession.cs`: contrato de sesión y entradas admitidas |
| S2 | `MIGRACION/EVIDENCIAS/F13_SR3_AUTONOMOUS_EXPORT_FIX_RESOURCE_RECOVERY_AND_FINAL_QA_01/DEV/UI_SR3_04_EDITOR/IntegratedWindow.cs`: arranque, acciones y selección de proyecto |
| S3 | Misma carpeta UI: `IntegratedPopulations.cs`, `IntegratedReviewLayout.cs` e `Integrated.WinUI.csproj`: rutas, rótulos, exportaciones y dependencias |
| S4 | `dotnet/src/SistemaMarcaciones.Desktop.Centro.WinUI/InstitutionalShell.cs`: navegación compartida |
| S5 | `dotnet/src/SistemaMarcaciones.Monthly.SourceIngestion/MonthlySourceIngestion.cs`: metadatos, cobertura y preparación de entrada causal |
| S6 | `dotnet/src/SistemaMarcaciones.Infrastructure/Referencia/Phase4Candidate.cs` y `CatalogoEstilos.cs`: consumidores heredados y estilo congelado |
| S7 | `src/marcaciones_cba/rules.py`: textos, horarios y colores del legado |
| S8 | `dotnet/src/SistemaMarcaciones.Monthly.Export.Eligibility/DisciplinePlan.cs`: proyección disciplinaria |
| S9 | `src/marcaciones_cba/monthly_review.py`, `novelty_events.py`, `institutional_documents.py`, `document_templates.py`: capacidades del legado, distintas del candidato SR3 |

## Matriz de conformidad

**Parcial** significa que existe una pieza de código relacionada y falta acreditar el recorrido completo en la aplicación entregada. **Incompatible** identifica una diferencia directa observada. **Pendiente** significa que no se acreditó ese requisito en el candidato inspeccionado. Ninguno de estos estados equivale a listo para producción. La columna final define la comprobación focal necesaria, no una prueba ejecutada.

| ID | Párrafos | Directriz | Estado del candidato | Evidencia y cierre necesario |
|---|---|---|---|---|
| R01 | P1–3 | ADMIN y OPADMIN: 08:00–16:30, almuerzo de 30 minutos dentro de 12:00–15:00 | Incompatible | S6/S7: estrategias distintas; comprobar ambos grupos con cuatro marcas y mismo horario |
| R02 | P3 | Un minuto tarde al ingreso genera un minuto y el detalle de ingreso | Parcial | S6: consumidor existente; verificar 08:00 y 08:01 en ruta real |
| R03 | P3 | Almuerzo de 31 minutos genera un minuto con detalle propio | Parcial | Verificar 30/31 minutos y separación de motivos, también OPADMIN |
| R04 | P3 | Salida 16:29 identifica un minuto de salida anticipada | Pendiente | Acreditar detalle sin confundir salida anticipada con ausencia de registro |
| R05 | P4 | Falta de ingreso: leyenda solicitada y rojo | Incompatible | S7 usa otra leyenda; preservar marca tardía como atraso según P3 |
| R06 | P5 | Falta de salida: leyenda solicitada y rojo | Parcial | S7: categoría disponible; probar registro ausente frente a salida anticipada |
| R07 | P6–7 | Distinguir falta de salida y retorno de almuerzo | Parcial | S7: textos existentes distintos; comprobar ambos campos y colores |
| R08 | P8,14 | Sin marcas: matriz roja y alerta con persona y fecha | Parcial | S5 impide confundir cobertura desconocida con falta; falta acreditar UI/exportación real |
| R09 | P9,20 | Cruzar todos los documentos antes de generar novedades | Parcial | S5/S6/S9: existen contratos; falta ingreso y aplicación integral desde interfaz productiva |
| R10 | P9 | Permiso personal por horas: salida anterior o retorno posterior fuera de matriz, CAMBIAR PERMISO | Pendiente | Probar 12:00–14:00 con retorno 14:05, cero en matriz si no hay otro motivo y alerta separada |
| R11 | P9 | Permiso médico por horas con la misma separación de alertas | Pendiente | Acreditar subapartado médico y diferencia de tiempo |
| R12 | P9 | Comisión por horas con cruce y alerta separada | Pendiente | Acreditar salida/retorno, cero y subapartado COMISIONES |
| R13 | P9 | Tabla de alertas: nombre, fecha, marcas relacionadas, mensaje, sugerencia, justificativo | Parcial | S2/S3 muestran centro de revisión genérico; acreditar todas las columnas y categorías |
| R14 | P9 | Permiso personal por días verde claro y médico celeste | Incompatible | S7: médico naranja; comprobar clasificación por día y consumidor visual/Excel |
| R15 | P9 | Licencias y vacaciones por período, morado y naranja | Parcial | S7/S9: categorías; acreditar aplicación exacta y cobertura parcial sin inventar fechas |
| R16 | P10 | Combinar la misma novedad del mismo servidor | Parcial | S7 tiene `document_merge_spans`; falta comprobar matriz generada y no unir personas diferentes |
| R17 | P11 | Más de cuatro marcas: texto y amarillo claro | Incompatible | S7 usa otra categoría y tono rosado; probar conteo y distinción de marcas duplicadas |
| R18 | P11 | Excepción individual de marcas extra indicada en el documento | Pendiente | Vincular excepción al maestro exacto, no a semejanza de nombres; no eximir otros incumplimientos |
| R19 | P11 | MARCACIONES EXTRA: todas las marcas y justificado por defecto X, editable a check | Pendiente | Probar guardar, cerrar y reabrir sin aprobación automática por importar un archivo |
| R20 | P11 | Justificativo en PDF, Word, Excel, imagen o texto | Pendiente | Acreditar adjunto/manual, vínculo exacto y reapertura; no publicar respaldos en GitHub |
| R21 | P12 | Suma de minutos y descuento con remuneración, traslado a matriz de valores | Pendiente | No acreditar mediante valores QA conservados. Reutilizar fórmula institucional verificable, no inventar divisor |
| R22 | P13 | Filas de valores según número de servidores, sin bordes vacíos | Parcial | S3 contiene gestión de filas económicas; falta recorrido con cambio real de plantilla y sin importes desconocidos convertidos en cero |
| R23 | P15 | Memorando ADMIN/OPADMIN cuando hay una o dos inconsistencias de las cuatro marcaciones | Pendiente | Generación Word y selección final no acreditadas en S2/S3; probar 0,1,2,3 inconsistencias |
| R24 | P16–17 | Un memorando por servidor y período, datos completos y dos novedades en tabla cronológica | Pendiente | Acreditar nombres, cargo/grado, período, fecha, tipo, marcas presentes/faltantes, justificativo, observación y acción |
| R25 | P18–20 | Excluir del requerimiento lo ya justificado | Pendiente | Generar únicamente después del cruce y de la confirmación documental |
| R26 | P19 | Word institucional listo para revisión/Quipux y plazo definido por DTH | Pendiente | Plantilla Word no disponible; no inventar membrete, firma, numerador o plazo, ni enviar a Quipux |
| R27 | P21 | Feriados: combinar columnas/días y filas de servidores, texto vertical rosado | Incompatible | S7 usa amarillo; falta verificar combinación física del libro final |
| R28 | P22–23 | Colores pastel legibles y cero sin relleno | Incompatible | S7 contiene rojo/verde intensos; comprobar estilos de UI y XLSX y cero numérico/textual |
| R29 | P24 | Respetar exactamente las plantillas institucionales | Parcial | Hay plantillas locales ADMIN y Directores; comparación del archivo final y formato de Operativos pendiente |
| R30 | P25–26 | Régimen: tres atrasos, diez minutos acumulados o tres inconsistencias simples/combinadas | Parcial | S8: umbrales relacionados; probar 2/3 eventos, 9/10 minutos y acumulación por corte sin cambiar evento por día sin acreditación |
| R31 | P27 | Régimen: datos personales, cada fecha y motivo concreto | Parcial | S8 expone brecha de presentación múltiple; cerrar detalle y plantilla sin convertir alerta en sanción automática |
| R32 | P28–29 | Directores: registrar novedades sin descuentos | Parcial | Ruta propia visible en S3; acreditar que ninguna exportación le aplica descuento |
| R33 | P30 | Director 08:01–16:31 con almuerzo 30 y ocho horas: sin atraso | Parcial | S6 identifica NETO_480_ALMUERZO_30; ejecutar con fuentes importadas por UI |
| R34 | P30 | Directores: exceso de almuerzo o menos de ocho horas sí genera novedad | Parcial | Verificar excepciones sin aplicar automáticamente estrategia ADMIN |
| R35 | P31 | Directores: mismos cruces documentales | Parcial | S3/S9: piezas separadas; acreditar permisos por horas y por días, comisiones, licencias y vacaciones |
| R36 | P32–33 | Operativos: cronograma 24×48, ingreso 07:00 y salida al día siguiente 07:00 | Pendiente | Ruta técnica visible en S3 no acredita cronograma real importable; probar 07:01 y 06:59 cruzando fecha |
| R37 | P34 | Permiso personal operativo por horas: alerta separada y cero si no hay otra novedad | Pendiente | Comprobar salida anterior, retorno 14:05 y preservación de otros incumplimientos independientes |
| R38 | P35 | Médico operativo: horas separadas, días celeste y diferencias detalladas | Pendiente | Acreditar ambos formatos y acción sugerida |
| R39 | P36 | Personal por días: fechas cruzadas con guardias y verde | Pendiente | No trasladar automáticamente horario administrativo a guardia |
| R40 | P37 | Comisión operativa por horas: salida/retorno, marcas ausentes y alerta separada | Pendiente | Acreditar intervalo autorizado y marcas relacionadas |
| R41 | P38 | Licencia operativa: período aplicado a guardia y morado | Pendiente | Acreditar cobertura exacta de jornada de dos fechas |
| R42 | P39 | Vacaciones operativas: fecha y hora exactas de reincorporación, incluso 22:00 | Pendiente | Probar regreso con/sin guardia y registro ausente; no usar solo fecha ni extender cobertura |
| R43 | P40 | Guardia cumplida: cero; dos días siguientes DESCANSO | Pendiente | Acreditar cronograma y ambos extremos antes de asignar cero/descanso |
| R44 | P40–41 | PERMISO, COMISIÓN, LICENCIA, VACACIONES, MONITOR, ASISTENTE, INSTRUCTOR, PERSONAL DE SEMANA con colores distintos | Pendiente | Catálogo y cruce con CFE; actividades no equivalen a aprobación sin documento válido |
| R45 | P41 | Importar CFE mensual con fechas y horarios, cruzar con cronograma y biométrico | Pendiente | Acreditar identidad, intervalo y justificación de ausencia en compañía |
| R46 | P42 | ECU 911: extremos de guardia y fracciones 15:00/23:00, movimientos institucionales | Pendiente | Verificar cada tramo y decir exactamente qué marca falta; 14:30/22:30 son ejemplos aproximados, no tolerancias nuevas |
| R47 | P43 | Dos pestañas operativas: cumplimiento general y solo inconsistencias | Pendiente | Acreditar Excel generado desde el mismo resultado y sin mezclar alertas con calendario general |
| R48 | P43 | Detallar regularización médica, de permisos y ECU con todos los registros | Pendiente | No aprobar automáticamente un permiso con cargo a vacaciones; proponerlo para revisión |
| R49 | P44 | Consolidado NOVEDADES con categorías y acción/justificativo/marcas | Pendiente | Separación efectiva en navegación, persistencia y exportación |
| R50 | P45–46 | Word operativo único por servidor con todas las inconsistencias cronológicas | Pendiente | Acreditar generación desde el cruce final, incluidos ECU y reincorporación |
| R51 | P47 | Excluir del memo guardias cumplidas, descanso y documentos/CFE sin inconsistencias | Pendiente | Comprobar cero requerimientos para jornadas y documentos válidos |
| R52 | P48 | Formato Word operativo institucional y plazo DTH, listo para revisión | Pendiente | Mismo pendiente de plantilla R26; no emisión automática |
| R53 | P49–50 | MATRICES DE NOVEDADES por mes, con tres grupos y revisión/consolidación/novedades/régimen | Incompatible | S2/S4: catálogo y navegación de sandbox, no árbol mensual solicitado |
| R54 | P51 | Crear trabajo eligiendo mes y disponer de todos sus subapartados | Incompatible | S1/S2: catálogo preadmitido y workspace existente obligatorios; agregar flujo productivo independiente |
| R55 | P52–54 | Inicio y Marcaciones como accesos principales | Parcial | S4 contiene ambos; falta integración con importación libre y trabajo mensual real |
| R56 | P55 | Permisos personales/médicos, cada uno por horas/días | Pendiente | S4 usa Documentos genérico; no acredita cuatro recorridos especializados |
| R57 | P56 | Comisiones por días/horas | Pendiente | Falta recorrido independiente vinculado al período activo |
| R58 | P57–61 | Licencias, Vacaciones, Justificaciones, Feriados y Configuración | Pendiente | Capacidades legadas S9 distintas de la UI SR3; conectar módulos reales, sin rótulos vacíos |
| R59 | P62 | Importar archivos en cada módulo y acumular registros nuevos | Incompatible | S1/S2 importan fuente predefinida; falta selector, vista previa, identidad, errores, confirmación y deduplicación |
| R60 | P62 | Formatos exigidos; conservar formatos propios de biométrico, permisos personales y comisiones | Pendiente | Contratos parsers disponibles parcialmente S5/S9; aceptar formatos reales, rechazar columnas incompatibles sin aplicar resultados |
| R61 | P62 | Conservar registros normalizados, no guardar el archivo importado | Pendiente | Auditar retención de bytes y temporales en todas las rutas. Nunca borrar el original seleccionado por el usuario |

## Pendientes de entrega adicionales de esta solicitud

| ID | Criterio | Estado |
|---|---|---|
| E01 | Inicio y uso completos sin Prueba sencilla, catálogo QA ni identidades ficticias obligatorias | Incompatible con S1/S2 |
| E02 | QA secuencial focal con límites de recursos, sin suites globales ni aperturas múltiples | Pendiente de cambios funcionales; esta revisión solo lee archivos pequeños |
| E03 | Instalación y datos protegidos, respaldo y reversión verificados del paquete exacto | Pendiente de paquete; esta sesión no instala ni modifica las bases institucionales |
| E04 | Código y compilación reproducibles en rama autorizada, sin fuentes personales en el repositorio público | Pendiente para aplicación; rama de revisión separada |
| E05 | Instalador/paquete, versión identificable, hashes y prueba de apertura/cierre del mismo artefacto | Pendiente; no existe nueva actualización final producida por esta revisión |

## Orden de implementación

1. Incorporar el código de aplicación a una estructura reproducible, seleccionando fuentes sin bases, documentos, logs ni secretos. Conservar los controles del sandbox como contrato de pruebas; construir una composición productiva distinta con importación real, identidad exacta y selección/creación de período. No promocionar resultados ni catálogos QA.
2. Conectar el árbol mensual y los módulos a servicios existentes donde su semántica sea compatible. Cada página debe realizar una operación real y recuperar su estado después de cerrar, sin depender de los archivos de una demostración.
3. Versionar las reglas nuevas de OPADMIN y de colores/textos, comprobar el cruce documental y completar las tres rutas. Reutilizar las plantillas locales comprobadas; los libros históricos no se sobrescriben.
4. Completar alertas, régimen y cálculo monetario trazable. Conservar el estado desconocido si falta salario, fórmula o cobertura; un número de ejemplo o una caché histórica no es un descuento calculado.
5. Completar memorandos una vez disponible la plantilla institucional, sin bloquear las funciones independientes. Mientras tanto, señalar esa función como pendiente en la aplicación y en la entrega.
6. Construir secuencialmente solo lo modificado y ejecutar la selección focal siguiente. Empaquetar el artefacto aprobado; verificar versión y hashes, apertura/cierre, respaldo y reversión. Solo entonces publicar la actualización final y sus notas exactas.

## QA focal de bajo consumo para la actualización

Una operación a la vez, sin compilaciones paralelas, sin reconstrucción de ensamblados inalterados y sin cambiar configuraciones del equipo. Reutilizar resultados previos solo cuando correspondan al mismo código/artefacto. No abrir Excel y la aplicación simultáneamente salvo que sea imprescindible para un caso concreto.

- Inicio sin entorno QA; nuevo mes; importación de fuente real no predefinida; importación repetida sin duplicados; formato inválido sin cambios; cierre/reapertura con datos persistidos.
- ADMIN y OPADMIN: ingreso 08:00/08:01, almuerzo 30/31, salida 16:30/16:29, cada marca faltante, cero marcas con cobertura confirmada y cobertura desconocida.
- Cruces: permiso 12:00–14:00 con retorno 14:05 y salida anterior; personal/médico/comisión; permiso diario/licencia/vacaciones; documentos superpuestos o no aprobados; otro incumplimiento independiente conservado.
- Marcas extra: cuatro/cinco, excepción individual, cambio X/check y respaldo persistido.
- Directores: ocho horas desplazadas, déficit y almuerzo excesivo; sin descuentos.
- Operativos: guardia entre fechas, 24×48, cronograma faltante, CFE, ECU parcial, retorno de vacaciones a hora expresa y las dos pestañas.
- Exportación: filas exactas, feriado combinado vertical, colores pastel/cero sin relleno y formatos preservados; verificar OOXML antes de una única apertura focal.
- Régimen: límites 2/3 y 9/10 y motivos múltiples; Word 0/1/2/3 para ADMIN y varias inconsistencias operativas, sin requerimientos por novedades ya justificadas.
- Instalación: usar copia aislada de datos y comprobar recuperación/reversión antes de tocar producción. Los datos usados para QA no se publican en GitHub.

## Lo realizado y lo no realizado

Se recuperó y leyó el documento completo, se inspeccionaron las superficies anteriores, se comprobó acceso al repositorio y se creó una rama de revisión. Se agregó un inspector estático reproducible que no inicia procesos de la aplicación ni modifica los archivos inspeccionados. No cambia el estado de ningún requisito a listo para producción.

No se eliminaron las protecciones del sandbox, no se sustituyó la instalación, no se escribieron datos institucionales, no se generó un memorando con formato inventado y no se publicó un instalador como actualización final. La dependencia de Prueba sencilla **continúa pendiente de eliminación funcional**; esta rama no la elimina por cambiar etiquetas ni por ocultar advertencias.
