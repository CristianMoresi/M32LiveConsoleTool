# Correcciones y mejora de M32 Live Console Tool

**Fecha:** 16 de octubre de 2026.\
**Base de comparación:** `804299018b09a32d3ecaed40e0d2c2a9012f9463`, versión 1.1.0.\
**Informe de partida:** [auditoría técnica](auditoria-m32-2026-10-16.markdown).\
**Alcance:** implementación, regresiones automatizadas, transporte UDP local, terminal interactiva, rendimiento y distribución portable.

## Resultado

Se han implementado correcciones para los 24 hallazgos del informe y las mejoras de control por dispositivo, diagnóstico, persistencia y CI. La suite pasa **158 de 158 pruebas**, frente a las 107 pruebas originales; se han añadido 51 casos y reparado pruebas que antes podían pasar sin verificar el comportamiento pretendido.

Los controladores requieren estado observado y reciente. Las escrituras pasan por una puerta común, el pánico la cierra antes de detener los módulos, los recursos de ganancia se comparten por previo físico y los cambios de sesión invalidan las cachés. El código separa la observación de la mesa de los comandos pendientes de confirmación.

La validación con una M32/X32 física sigue pendiente. Las pruebas locales cierran las reproducciones de software; no certifican firmware, stage boxes, latencia acústica ni todos los estados posibles de una mesa real.

## Trazabilidad de los 24 hallazgos

Las referencias de pruebas siguientes están en [AuditRegressionTests.cs](../tests/M32LiveConsoleTool.Tests/AuditRegressionTests.cs), salvo donde se indica [TransportRecoveryTests.cs](../tests/M32LiveConsoleTool.Tests/TransportRecoveryTests.cs) o verificación de terminal.

| ID | Corrección implementada | Evidencia de aceptación |
| --- | --- | --- |
| F01 | Separación de fader observado, último comando y corrección acumulada. El estado de envío solo cambia tras un envío local aceptado. | `F01_F24_smooth_steps_accumulate_without_false_manual_override`: velocidades de 0,1/0,5/2 dB/s, varios tramos de la curva y ciclos de 10/25/50/100 ms. |
| F02 | El anticlipping exige ruta, ganancia, mute y meters válidos. Una fuente sin resolver no se trata como digital. | `F02_no_gain_write_until_source_and_gain_are_confirmed`. |
| F03 | Consulta y seguimiento del trim digital; las reducciones parten del valor observado. | `F03_digital_trim_reduces_the_observed_value` y cambio real de routing en UDP local. |
| F04 | `GainState` compartido por previo: ganancia, confirmación, enfriamiento y presupuesto de reducción comunes. | `F04_shared_gain_and_cooldown_prevent_conflicting_reductions`. |
| F05 | Las notificaciones de fuente/routing/escena invalidan el estado. La ruta también caduca si dejan de llegar respuestas aunque siga llegando la ganancia antigua. | `F05_F06_routing_and_peer_changes_invalidate_observed_state`; `Missing_routing_replies_block_gain_control_even_when_old_gain_keeps_arriving`. |
| F06 | Generación de sesión e invalidación de faders, mute, meters, ganancias, efectos, tempo y comandos pendientes al cambiar de destino o resincronizar. | Regresión F05/F06 y guardas de generación de cada módulo. |
| F07 | Mute desconocido bloquea las actuaciones. Las consultas de fader/mute se renuevan también para buses. | `F07_unknown_mute_blocks_both_controllers`; pérdida inicial de respuestas en `TransportRecoveryTests`. |
| F08 | Solo se escribe tiempo sobre FX 1–4 con tipo compatible y confirmado recientemente. Un tipo desconocido o caducado suspende el control. | `F08_unknown_changed_or_stale_effects_do_not_receive_time_writes`; recuperación de tipo/tiempo por UDP. |
| F09 | Salud por dispositivo y caducidad de meters. Los blobs incompletos, alias desconocidos o valores no finitos no renuevan la salud. | `F09_stale_samples_suspend_controls_and_unknown_blobs_do_not_mark_health`. |
| F10 | Historial acotado de comandos para reconocer ecos atrasados. Un comando sin confirmación caduca; una intervención ajena inicia estabilización. | `F10_out_of_order_echoes_are_distinct_from_manual_changes`; regresión de envío fallido. |
| F11 | El receptor exige IP y puerto del destino seleccionado antes de entregar feedback. | `F11_only_the_selected_peer_can_publish_feedback` en `TransportRecoveryTests`: un segundo socket no puede cambiar el estado. |
| F12 | Validación de números finitos en configuración, propiedades, conversión, meters y salida. | `F12_non_finite_parameters_are_rejected` para NaN y ambos infinitos, además de tests de configuración y parser. |
| F13 | El preset aplica ventana, parámetros, dispositivos, slots y sus flags ON/OFF. Construye la nueva configuración antes de sustituir la activa; conserva la red y un pánico ya activado. | `F13_preset_applies_window_flags_and_manual_delay_configuration`. |
| F14 | Reparación de colecciones y elementos nulos, enumeraciones, rangos y campos nuevos. | `F14_null_collections_and_elements_are_repaired`; ida/vuelta de opciones por dispositivo. |
| F15 | Serialización previa y sustitución mediante temporal en la misma carpeta, con flush a disco. El cierre informa de error y no anuncia un guardado inexistente. | `F15_failed_serialization_preserves_the_previous_session`. No simula un corte eléctrico real. |
| F16 | Retención de 11 s para ventanas de hasta 10 s, con límite adicional de 4.096 muestras por dispositivo. Promedio sin copiar el historial. | `F16_ten_second_window_and_average_without_history_copy`; microbenchmark comparable. |
| F17 | Cuantización hacia la atenuación sobre la rejilla de ganancia. Una reducción aceptada elimina al menos un paso físico y respeta el presupuesto. | `F17_small_reductions_remove_at_least_one_physical_gain_step`: previo y trim. |
| F18 | Cursores por secuencia para procesar todas las muestras nuevas, incluidas las recibidas entre dos ciclos. | `F18_peaks_between_poll_cycles_are_processed`; `F18_bpm_consumes_every_new_edge_in_a_batch`. |
| F19 | IPv4 validada antes de construir o cambiar el transporte. IPv6 se rechaza sin iniciar un worker incompatible. | `F19_ipv6_is_rejected_before_a_worker_can_start`; tests de configuración. |
| F20 | BPM se recalcula con nueva evidencia; tempo con vigencia de 4 s y deduplicación del tiempo efectivo. Consulta y reintento si falta confirmación. | `F20_zero_hysteresis_does_not_reprocess_old_taps_and_tempo_expires`. |
| F21 | El simulador aplica previo/trim y fader a una misma cadena de señal; añade cuantización, mute, routing digital/compartido, caducidad de suscripciones y pérdida programable. | `F21_simulated_preamp_attenuates_both_meters_and_digital_routing_is_observed` en `TransportRecoveryTests`. |
| F22 | Entrada centralizada: F12 cierra la puerta de escritura, cancela formularios y detiene los módulos. Los delays quedan suspendidos hasta un rearme explícito apropiado. | `F22_emergency_key_closes_the_gate_and_cancels_the_form`; prueba de no rearme de delays al activar solo el nivelador; terminal real descrita abajo. |
| F23 | Puertos efímeros y esperas por condición en integración; pérdida deliberada de la primera respuesta de cada parámetro, sin depender de un paquete inicial. | `F07_F23_initial_reply_losses_recover_mute_gain_meters_and_all_required_fx` en `TransportRecoveryTests`; integración existente actualizada. |
| F24 | Espera cancelable hasta el siguiente ciclo y cálculo del slew por tiempo monotónico transcurrido, con límite de recuperación después de una pausa. | Regresiones F01/F24, salto de reloj civil y pausa larga. |

## Mejoras adicionales entregadas

### Control y operación

- Cada canal/bus permite activar el nivelado y fijar un techo de fader; cada canal permite activar la protección y limitar la reducción acumulada. Se editan desde **N → E** y se conservan en sesión y presets.
- Si varios canales comparten previo, el límite más estricto de los canales vigilados gobierna el recurso compartido. Excluir un canal de la protección evita que ese canal dispare reducciones; otro canal del mismo previo todavía puede reducirlo. El panel indica los recursos compartidos.
- El contador de reducción acumulada corresponde al estado sincronizado actual: vuelve a empezar al reiniciar ese estado por resincronización, cambio de conexión, routing o configuración de dispositivos. No es un presupuesto persistente entre sesiones.
- Los comandos de ganancia esperan confirmación. Si un datagrama se pierde, se reintenta la misma atenuación después de una respuesta reciente, sin contabilizar dos veces la reducción ni subir la ganancia.
- El techo del fader respeta la velocidad de bajada. La acumulación de pasos pequeños tampoco puede sobrepasar un techo más cercano que el umbral de envío.
- La carga de un preset presenta un resumen antes de aplicarlo. Las opciones ON/OFF del preset se aplican exactamente, pero no levantan por sí solas un pánico activo.
- El dashboard muestra sincronización, ausencia de señal, control liberado, estado desconocido y exclusión por dispositivo. Preferencias → V muestra RX/TX, paquetes rechazados, fallos de envío y descartes del log.
- El tap manual funciona aunque no haya canales vigilados; el mantenimiento de suscripciones no invalida continuamente ese caso de uso.

### Rendimiento y mantenimiento

- `GetLatestSample()` accede a la última muestra sin crear una lista. El promedio recorre el historial bajo bloqueo y toma un único instante de referencia, sin asignar una copia por consulta.
- OSC codifica en un único buffer con operaciones de `Span`/`BinaryPrimitives`. El parser comprueba límites y terminación de cadenas. Hay paquetes binarios de referencia escritos independientemente del encoder para tipos entero y flotante.
- El endpoint se valida y conserva; las escrituras y las transiciones de estado se serializan mediante la misma puerta de control.
- El log de disco usa una cola acotada de 1.024 eventos y un escritor en segundo plano. El anillo conserva los últimos 50 eventos y la pantalla filtra los importantes; una saturación o fallo de disco se hace visible. El cierre fuerza el vaciado con tiempo acotado.
- El reloj de control, la antigüedad de muestras, el enfriamiento y el uptime son monotónicos. Los cambios del reloj civil no alteran esas decisiones.
- `global.json` fija la familia estable de SDK .NET 10. CI compila y prueba en Windows/Linux/macOS, conserva TRX/cobertura y publica/ejecuta un binario autocontenido en cada runner. La publicación de releases depende de esta matriz.
- `--self-test` permite comprobar el portable sin terminal interactiva ni mesa física. Acepta de 1 a 3.600 segundos y emite métricas JSON.

## Resultados de validación

### Suite, cobertura y compilación

Entorno local: Windows x64, SDK 10.0.302, runtime 10.0.10. Compilación Release y publicación autocontenida correctas, sin advertencias de compilador ni errores. Suite final: **158 aprobadas, 0 fallidas, 0 omitidas**.

| Medida | Auditoría inicial | Tras las correcciones |
| --- | ---: | ---: |
| Pruebas existentes/actuales | 107 | 158 |
| Pruebas aprobadas en la ejecución conservada | 105 | 158 |
| Cobertura de líneas | 32,64 % | 47,92 % |
| Cobertura de ramas | 28,70 % | 46,11 % |
| Líneas ejecutadas / instrumentadas | 1.239 / 3.795 | 1.982 / 4.136 |
| Ramas ejecutadas / instrumentadas | 521 / 1.815 | 1.111 / 2.409 |

La cobertura sigue siendo parcial, especialmente en menús interactivos y rutas de fallo del sistema operativo. La subida de porcentaje no sustituye las regresiones concretas ni demuestra ausencia de otros errores. El código instrumentado también ha cambiado, por lo que los denominadores son distintos.

Evidencias: [TRX final](validation-2026-10-16/regressions.trx), [resumen JSON](validation-2026-10-16/test-summary.json), [Cobertura XML](validation-2026-10-16/coverage.cobertura.xml).

### Terminal interactiva

Se inició el programa en una terminal ConPTY con simulación y se verificaron estas secuencias:

1. Activar el nivelador, entrar en alta de canal y pulsar F12 durante el formulario: vuelve al menú, deja los módulos apagados y no añade el canal incompleto.
2. Pulsar F12 durante el aviso de actualización de FX: cierra el aviso y deja el control liberado.
3. Pulsar F12 durante la captura/recalibración de objetivos: cancela la espera y deja los módulos apagados.
4. Cerrar con Q y confirmación: termina correctamente y conserva el aislamiento entre la sesión simulada y la sesión real.

Esto verifica las entradas y esperas interactivas probadas. No se ha medido el tiempo de lectura de teclado durante un bloqueo de disco o del sistema operativo.

### Microbenchmark comparable de promedios

Carga idéntica: 48 dispositivos, 100 muestras por dispositivo y 48.000 consultas. Los datos iniciales están en [probes.json](../tests/audit-2026-10-16/evidence/probes.json); los nuevos, en [average-benchmark-after-2026-10-16.json](average-benchmark-after-2026-10-16.json).

| Medida | Antes | Después |
| --- | ---: | ---: |
| Asignaciones del hilo | 41.088.040 bytes | 6.360 bytes |
| Tiempo total observado | 207,3893 ms | 110,4519 ms |

La copia por consulta desaparece: aproximadamente **99,98 % menos bytes asignados** en esta medición. El tiempo observado baja alrededor del 47 %, pero es una ejecución local, con otras tareas activas, sin distribución estadística suficiente para garantizar ese factor en todos los equipos. El algoritmo del promedio sigue siendo O(n) respecto al historial; no se presenta como un promedio incremental O(1).

### Ejecutable portable final

El EXE autocontenido para Windows se publicó y ejecutó durante 12 segundos sobre 32 canales y 16 buses. Resultado: 48 dispositivos y 32 ganancias sincronizados, 0 comprobaciones degradadas, 0 fallos de envío, 0 descartes de log y pánico correcto. Durante los 150 ms posteriores a inhibir las escrituras no se registraron nuevos comandos de control.

Medición: 12,049 s; 734,375 ms de CPU (6,09 % de un núcleo); 13.081.584 bytes asignados; 8.394 mensajes recibidos; 0 colecciones GC durante el intervalo; 0,2299 ms para cerrar la puerta de escritura. Edad de meters muestreada: p50/p95/p99 de 15/15/15 ms. Evidencia completa: [portable-self-test-2026-10-16.json](portable-self-test-2026-10-16.json).

El perfil inicial de 12 s medía aproximadamente 3 % de un núcleo y 23,57 MB asignados, pero usaba otro modelo de simulación y otro ritmo de sincronización. Por eso **no se afirma una mejora global de CPU** comparando ambos perfiles. La edad de los meters mide antigüedad de la última muestra en el proceso, no latencia de audio ni tiempo extremo a extremo de respuesta ante clipping.

### Sesión continua de 30 minutos

La ejecución completó **1.800,003 s** y terminó con código 0. Permanecieron listos 48 dispositivos y 32 ganancias, con **0 comprobaciones degradadas**, 0 fallos de envío, 0 paquetes rechazados y 0 eventos de log descartados. Se recibieron 1.242.236 mensajes. El pánico cerró la puerta de escritura en 0,414 ms y no hubo nuevos comandos de control durante la comprobación posterior de 150 ms.

CPU acumulada: 36.625 ms, equivalente al **2,03 % de un núcleo**. Asignaciones acumuladas: 1.925.221.328 bytes en media hora; esta cifra es volumen asignado, no memoria residente. Colecciones GC por generación: 38/26/1. Antigüedad muestreada de meters p50/p95/p99: 61/61/62 ms. Los 48.000 promedios finales necesitaron 25,6693 ms y 0 bytes asignados en el hilo medido.

La carga fue estable, con entrada fija a −15 dB y excursiones de clipping desactivadas. Los tres módulos estaban activos, pero solo se emitieron 2 comandos de control: comprueba continuidad de recepción, sincronización y workers, no la peor carga de correcciones posibles. Los casos de clipping, pérdidas y recuperación se ejercitan en pruebas separadas. Evidencia: [soak-30min-2026-10-16.json](soak-30min-2026-10-16.json).

La sesión larga usa un build aislado de las correcciones principales, iniciado antes de los últimos ajustes de caducidad de routing, tap sin canales y techo cercano de fader. Esos ajustes están cubiertos por la suite final y por la prueba del portable final. Esta distinción evita atribuir a la sesión larga una revisión que no ejecutó.

## Reproducción

Desde la raíz del repositorio:

```sh
dotnet test M32LiveConsoleTool.slnx -c Release --logger trx --collect:"XPlat Code Coverage" --results-directory tests/TestResults
dotnet run --project tests/audit-2026-10-16/AuditProbe.csproj -c Release -- --benchmark
dotnet run -c Release -- --self-test 12
dotnet run -c Release -- --self-test 1800
dotnet publish M32LiveConsoleTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/portable
```

Para otros sistemas, cambiar el RID por el correspondiente al equipo y ejecutar el portable con `--self-test`. CI hace esta comprobación en cada plataforma. El modo diagnóstico no carga presets, no guarda la última sesión y solo dirige tráfico a loopback.

Las sondas históricas usan miembros privados de la base auditada. Su finalidad y reproducción se explican en [tests/audit-2026-10-16/README.markdown](../tests/audit-2026-10-16/README.markdown). No se deben ejecutar para sobrescribir la evidencia inicial con la implementación actual.

## Límites pendientes de comprobar con hardware

Antes de dar por validada la operación en directo hay que contrastar en una M32/X32 física:

1. Que el firmware y los stage boxes usados devuelvan IP/puerto, índices de previo y cuantización conforme a los supuestos del protocolo.
2. Routing local/AES50/digital, cambio de escena, conexión perdida y recuperada, dos canales compartiendo previo y cargas parciales de estado.
3. Mute groups, DCA, stereo links y procesamiento de canal. El simulador no representa toda esa cadena.
4. Faders manuales con ecos retardados/reordenados y audibilidad de las velocidades configuradas. OSC carece de identificadores únicos de confirmación; un movimiento idéntico a un comando reciente puede ser indistinguible de su eco.
5. Los FX compatibles y sus parámetros con capturas reales; la prueba binaria independiente del parser no sustituye una traza completa de firmware.
6. Respuesta de protección ante señal real, con ganancia conservadora y supervisión. El anticlipping reacciona a meters: no es un limitador de audio y no puede evitar todos los transitorios.

También quedan fuera de esta ejecución los cortes eléctricos durante persistencia, fallos físicos de disco y un perfil completo de rendimiento que incluya dashboard y log de disco. La cola y la escritura atómica reducen riesgos, pero no equivalen a ensayos de esos fallos.

## Entrega y conservación

Se conserva el informe inicial como evidencia histórica y este registro como estado posterior. Ambos están en formato `.markdown`. No se genera ni conserva el PDF de auditoría; los PDF de documentación del producto y del protocolo no se alteran.

El cambio previo del usuario en `.gitignore` se conserva y queda fuera del commit de correcciones. Los binarios, temporales de pruebas y perfiles intermedios de compilación permanecen fuera de Git. Las evidencias seleccionadas de auditoría y validación se versionan para poder contrastar el resultado.
