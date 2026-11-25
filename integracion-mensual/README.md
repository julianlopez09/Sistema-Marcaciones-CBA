# Integración mensual CBA 0.8.1.400 — F13 abierta

Fuentes de integración no congeladas para la aplicación Windows mensual. La rama conserva el historial y no sustituye las versiones anteriores.

## Alcance

Navegación en español, creación de períodos sin ejemplos precargados, importación de personal y jornadas, biométricos y documentos estructurados, captura de resultados de los consumidores C# existentes, matrices, Centro, revisión separada, exportación Excel y borradores Word. El inicio normal y la prueba anónima tienen datos y perfiles distintos. Una revisión automática de QA no puede convertirse en revisión normal.

Los motores, contratos congelados, Storage V2 y bibliotecas admitidas se reutilizan como dependencias. Esta carpeta no contiene su código ni pretende reemplazarlos. No se ejecuta D08 ni se generan descuentos nuevos.

## Compilación controlada

Las fuentes C# de esta carpeta corresponden a las fuentes compiladas registradas en SOURCES.json. Los proyectos parametrizan las rutas privadas mediante `CbaPrivateSourceRoot`; son necesarios el repositorio privado admitido y sus dependencias exactas. También deben proporcionarse las propiedades AppDir, AR1CoreBin, CbaFrozenB07Root y las demás rutas de referencias que aparecen en el proyecto WinUI. La biblioteca de integración se compila primero y la interfaz después, en un único proceso de compilación por vez. No ejecutar Clean/Rebuild de motores congelados.

Se utilizó .NET SDK 10.0.401, runtime 10.0.13 y Microsoft.WindowsAppSDK 1.8.260317003 con cachés ya calificadas. El producto requiere Windows App Runtime 1.8 x64 del usuario. Las dependencias de terceros, los formatos con materiales institucionales, las configuraciones de cuenta, las políticas de autorización y los datos no se publican aquí. Esta rama no acredita una compilación autónoma a partir de un clon público ni una validación en Windows limpio.

## Restricciones

Guardia real espera calendario, nómina, servicio efectivo y contexto D/D+1 acreditados; los siete B06 siguen bloqueados. Determinados respaldos (reincorporación exacta de vacaciones, cambios de Guardia, ECU 911, asistencia manual y feriados) mantienen su aplicación pendiente del contrato específico. Los documentos Word son borradores: faltan aceptación del formato institucional, firma y emisión por el responsable.

QN11 solo cubre la frontera propia instrumentada; su parte temporal externa permanece PENDING_NOT_PROVEN. La prueba local no equivale a Windows limpio. No se declara producción, instalación institucional aprobada, cierre de F13, F14 ni cutover.

El ejecutable se conserva en el destino local paralelo autorizado. Su distribución externa permanece bloqueada por derechos y tratamiento de materiales protegidos. Esta publicación de código no otorga derechos adicionales sobre logos, plantillas, documentos institucionales ni bibliotecas de terceros.