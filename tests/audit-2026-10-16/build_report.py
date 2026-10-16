from pathlib import Path
from collections import Counter
import json, html, math

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'output/pdf'
OUT.mkdir(parents=True, exist_ok=True)
EVIDENCE = ROOT / 'tests/audit-2026-10-16/evidence'
probes = json.loads((EVIDENCE/'probes.json').read_text())
profile = json.loads((EVIDENCE/'load-profile.json').read_text())
findings=[]
def finding(id, priority, title, refs, evidence, impact, cause, fix, acceptance, status='Reproducido'):
    findings.append(dict(id=id, priority=priority, title=title, refs=refs, evidence=evidence, impact=impact, cause=cause, fix=fix, acceptance=acceptance, status=status))

finding('F01','P1','El autonivelado suave se confunde con un movimiento manual',
 'Modules/AutoLeveler.cs:470-523; Domain/MixerChannel.cs:96-105; Domain/AudioDevice.cs:312-314',
 'A01: fader a -20 dB, señal a -24 dBFS, objetivo -18 dBFS y ajustes por defecto (0,5 dB/s, 100 ms). Tras cuatro evaluaciones: cero mensajes y estado ManualOverride.',
 'La opción Smooth puede dejar de corregir y, tras estabilizarse, adoptar como objetivo la desviación que debía corregir. También afecta a buses y a otros ajustes de velocidad o posición.',
 'ApplyCorrection cambia LastSentFader antes de comprobar FADER_EPSILON. El paso de 0,05 dB equivale a 0,000625 en este tramo: se descarta el envío, pero se conserva la modificación interna. La diferencia acumulada acaba pareciendo manual.',
 'Separar posición observada, último comando enviado y corrección pendiente. Acumular pasos pequeños sin modificar el estado de envío; enviar al alcanzar la resolución real del fader. Actualizar el comando pendiente solo cuando se acepta para transmisión.',
 'Con 0,1/0,5/2 dB/s y faders en los cuatro tramos de la curva, la señal converge sin intervención manual ficticia ni pasos superiores al límite. Verificar datagramas y estado, no solo LastSentFader.')
finding('F02','P1','El anticlipping actúa antes de resolver el previo y su ganancia',
 'Modules/AntiClipper.cs:445-503,623-718; Modules/MeterReader.cs:906-919; Domain/MixerChannel.cs:29-37',
 'A02: HeadAmpResolved=false y ganancia almacenada de +12 dB. Una muestra de clip provoca /ch/01/preamp/trim=0,75, equivalente a +9 dB, sin conocer la fuente.',
 'La protección puede escribir el control equivocado o aumentar una ganancia real que era inferior a la copia local. Contradice la garantía de que solo atenúa.',
 'HeadAmpIndex=-1 significa tanto fuente digital como estado desconocido. EvaluateChannel no consulta HeadAmpResolved. Además, recibir el índice marca la resolución antes de recibir la ganancia; si esta respuesta se pierde, no existe un reintento específico.',
 'Modelar por separado fuente desconocida, fuente resuelta y ganancia confirmada. Bloquear la actuación hasta confirmar ambos datos; invalidarlos al reconectar. Reintentar consultas con un plazo acotado y registrar el estado pendiente.',
 'Retrasar o perder las respuestas de índice y ganancia: ningún comando de reducción debe salir. Al confirmar el estado, el primer valor escrito debe ser menor o igual a la ganancia confirmada.')
finding('F03','P1','Las fuentes digitales no sincronizan su trim',
 'Modules/MeterReader.cs:899-938; Modules/AntiClipper.cs:699-718',
 'A03: al recibir índice -1 no se consulta el trim. Una respuesta /ch/01/preamp/trim con valor 0,0 (-18 dB) se ignora y el estado permanece en 0 dB.',
 'Con trim real -18 dB y caché 0 dB, una supuesta reducción de 3 dB enviaría -3 dB: un incremento real de 15 dB. También se pierden los cambios manuales de trim.',
 'El lector solo trata /headamp/.../gain y /-ha/.../index. La alternativa digital utiliza el mismo PreampGain sin sembrarlo desde la mesa ni recibir sus cambios.',
 'Consultar y procesar /ch/NN/preamp/trim para fuentes digitales, con límites y cuantización propios. Mantener la identidad y la validez del control de ganancia; no reutilizar un valor de head amp como si fuese trim.',
 'Cubrir trims iniciales -18, -6, 0 y +18 dB, cambios manuales y cambio de fuente analógica a digital. El valor real nunca aumenta por una orden del anticlipping.')
finding('F04','P1','Un previo compartido deja canales con ganancias incoherentes',
 'Modules/MeterReader.cs:926-938,970-981; Modules/AntiClipper.cs:633-680',
 'A04: dos canales asociados a HA0 reciben un cambio a -12 dB. Solo el primero se actualiza; el segundo continúa en 0 dB.',
 'Si el segundo canal detecta clip, puede ordenar -3 dB al previo que ya estaba a -12 dB. La reducción calculada localmente se convierte en una subida de 9 dB para todas sus rutas.',
 'FindChannelByHeadAmpIndex devuelve una sola coincidencia. El recurso físico y su cooldown se representan como estados independientes por canal.',
 'Crear un estado de ganancia por headAmpIndex y asociar a él todos los canales que lo comparten. Consolidar solicitudes de atenuación por recurso físico, con cooldown y confirmación comunes; reflejar cada respuesta en todos sus consumidores.',
 'Con dos canales y dos órdenes cercanas sobre el mismo previo, emitir una decisión coherente y nunca deshacer una reducción anterior. Mostrar qué canales y rutas comparten el recurso.')
finding('F05','P1','Un cambio de routing no invalida el previo seleccionado',
 'Modules/MeterReader.cs:718-727,733-781,899-938',
 'A22: tras resolver HA0, una notificación /ch/01/config/source=33 no invalida nada. RequeryUnresolvedHeadAmps no envía consultas y se conserva HA0.',
 'Si la mesa no envía además un nuevo índice /-ha, el anticlipping seguirá actuando sobre la entrada antigua, que puede alimentar otro canal. El alcance real depende de las notificaciones del firmware.',
 'El seguimiento reconoce fader, mute e índice de previo, pero no cambios de fuente, bloques de routing o escenas. Solo reconsulta índices marcados como no resueltos.',
 'Invalidar la asociación ante cambios de fuente/routing o de escena y detener las actuaciones afectadas hasta verificar el nuevo recurso y su ganancia. Añadir una comprobación periódica conservadora como respaldo.',
 'Reproducir mensajes de routing y después verificar con M32/X32 y AES50. Hasta confirmar el nuevo índice, no debe escribirse ni el previo antiguo ni uno deducido de la numeración del canal.')
finding('F06','P1','Cambiar de mesa conserva estado de la conexión anterior',
 'Ui/AppOrchestrator.cs:677-693; Modules/MeterReader.cs:239-262; Domain/AudioDevice.cs:282-289; Modules/DelayController.cs:181-186',
 'A18: cambiar la IP conserva FaderInitialized=true, el historial de muestras y el catálogo de efectos detectados.',
 'Los módulos activos pueden enviar a la nueva mesa órdenes calculadas con datos de la anterior. Si la mesa antigua sigue transmitiendo, el receptor tampoco la distingue del nuevo destino (F11).',
 'SetMixerIp cambia el destino y reconsulta, pero no establece una barrera de transición. ResubscribeAll solo invalida HeadAmpResolved; no resetea fader, mute, muestras ni efectos.',
 'Usar una generación de conexión: suspender escrituras, cerrar o cancelar la sesión anterior, vaciar estados observados y resincronizar. Publicar el estado listo de forma atómica antes de reactivar módulos según la política del operador.',
 'Dos mesas simuladas con faders, mute, routing y FX distintos: ninguna escritura basada en la primera debe alcanzar la segunda. Aplicar también a pérdida/reanudación prolongada de red.')
finding('F07','P1','Una respuesta de mute perdida deja el dispositivo habilitado',
 'Domain/AudioDevice.cs:84-93,298-300; Modules/MeterReader.cs:673-678,698-727,864-870; Modules/AutoLeveler.cs:452-460',
 'A20: con fader y head amp conocidos, perder la respuesta inicial de mute deja IsMuted=false y los reintentos periódicos no consultan el mute.',
 'El nivelador o el anticlipping pueden modificar un canal realmente silenciado, aunque ambos prometen respetar su mute. No es necesario que falle el flujo de meters.',
 'El estado de mute solo es booleano: no distingue desconocido de abierto. Se consulta inicialmente, pero únicamente se reintentan faders e índices de previo no resueltos.',
 'Añadir estado de validez de mute, bloquear las actuaciones mientras sea desconocido y reintentar hasta obtener una respuesta. Revisar además la semántica de mute por DCA/grupo según el alcance deseado.',
 'Perder exclusivamente el paquete /mix/on, manteniendo el resto del tráfico. Ningún módulo debe actuar hasta conocer el mute; al confirmar on=0, debe seguir inhibido.')
finding('F08','P1','Los delays escriben con efectos desconocidos u obsoletos',
 'Modules/DelayController.cs:254-259,408-434; Ui/AppOrchestrator.cs:300-317',
 'A08: un slot manual de 500 ms escribe /fx/1/par/02 sin haber detectado ningún efecto. A09: recibir tipo 999 después de Stereo Delay mantiene el tipo antiguo y permite otra escritura.',
 'Un preset antiguo o una detección tardía puede alterar un parámetro de un reverb u otro efecto. Que el envío sea numéricamente válido no garantiza que modifique el tiempo de delay.',
 'Las consultas de efectos son asíncronas y el arranque aplica los tiempos manuales inmediatamente. El fallback se utiliza automáticamente; las respuestas desconocidas no invalidan la caché y RefreshEffects tampoco la borra.',
 'Retener las órdenes hasta identificar un delay compatible en la conexión actual. Invalidar tipos desconocidos o caducados. Si se permite fallback manual, exigir una selección explícita vigente y presentarla como no verificada.',
 'Arrancar con reverb, perder la consulta de tipo, cambiar a tipo desconocido y reconectar con otro rack: cero escrituras hasta resolver el destino. Al resolverlo, aplicar la última intención pendiente.')
finding('F09','P2','La salud de meters no gobierna las actuaciones',
 'Modules/MeterReader.cs:168-174,428-460,768-779; Modules/AutoLeveler.cs:478-523',
 'A05: una muestra de 2.200 ms permite una corrección cuando IsReceivingMeters ya es false. A06: alias desconocido con blob truncado vuelve a marcar recepción correcta.',
 'El panel puede mostrar NO SIGNAL mientras aún se mueve un fader. Tráfico inválido o un solo canal activo pueden ocultar la caída de otras suscripciones.',
 'El promedio usa su ventana temporal, pero el módulo no consulta frescura individual. lastMeterTickMs se actualiza antes de validar alias, tamaño útil y distribución de la muestra.',
 'Marcar recepción únicamente tras aceptar una muestra válida y asociada a un dispositivo. Mantener tiempos de última muestra por canal y estado de enlace; suspender decisiones con datos vencidos y reintentar las suscripciones que faltan.',
 'Probar caída total, pérdida de un único alias, blobs truncados y recuperación parcial. La decisión de escribir y el indicador visible deben usar la misma definición de frescura.')
finding('F10','P2','Los ecos atrasados se interpretan como intervención del técnico',
 'Domain/AudioDevice.cs:282-314; Modules/AutoLeveler.cs:470-474,521-523,531-581',
 'A14: después de dos correcciones, llega el eco de la primera. La siguiente evaluación entra en ManualOverride sin ningún movimiento externo.',
 'La latencia o el reordenamiento UDP pueden parar la automatización y recalibrar el objetivo incorrectamente. La escritura optimista también oculta órdenes que nunca llegaron a la mesa.',
 'Se compara cada feedback con un único LastSentFader; CurrentFader se sobrescribe localmente antes de recibir confirmación. No hay historial de comandos pendientes ni política de conciliación.',
 'Separar posición observada e intención de control. Mantener una ventana acotada de valores enviados y sus tiempos, aceptar ecos plausibles y tratar las discrepancias conservadoramente. OSC no proporciona identificadores de confirmación, por lo que hay ambigüedad que debe explicitarse.',
 'Inyectar latencia, duplicados y reordenamiento, junto a movimientos manuales reales. La automatización debe reconocer sus ecos sin impedir que el operador tome el control.')
finding('F11','P2','Se acepta OSC desde cualquier origen de red',
 'Osc/OscClient.cs:137-145,260-291; Modules/MeterReader.cs:788-833',
 'A16: un emisor loopback distinto del destino configurado envía un fader de 0,9 y modifica inmediatamente el estado del canal.',
 'Otro programa, una mesa anterior o un equipo de la LAN puede inyectar valores de control o meters. No se ha probado una exposición a Internet; el riesgo observado es la falta de filtrado del peer.',
 'El socket escucha en todas las interfaces y ReceiveLoop no compara remoteEndpoint con el destino configurado antes de invocar los suscriptores.',
 'Resolver el endpoint y filtrar IP/puerto de origen según el contrato real de la mesa; separar la política del simulador. Invalidar el peer al cambiar de conexión y contar los descartes. El filtrado no equivale a autenticación criptográfica.',
 'El emisor esperado actualiza el estado; otros peers no lo hacen. Verificar IPv4, direcciones normalizadas, cambio de IP y respuestas de hardware antes de fijar la regla de puerto.')
finding('F12','P2','Los valores no finitos contaminan el control',
 'Modules/MeterReader.cs:433-460,986-1043; Domain/AudioDevice.cs:282-288; Modules/AutoLeveler.cs:584-588; Infrastructure/SessionConfig.cs:129-150',
 'A07: una muestra NaN seguida de una válida produce promedio NaN. El parser valida longitud, pero acepta floats no finitos; también se aceptan valores de fader fuera de rango.',
 'Math.Sign(NaN) puede lanzar una excepción en el nivelador y cortar el ciclo completo, dejando sin evaluar los dispositivos posteriores. Infinity puede producir correcciones máximas o persistencia inválida.',
 'Muchas comprobaciones solo comparan límites; NaN supera indirectamente esas barreras porque ambas comparaciones resultan falsas. No se valida la semántica de cada campo al entrar.',
 'Aplicar IsFinite y rangos específicos en fronteras OSC/configuración. Rechazar frames inválidos sin refrescar su salud. Aislar errores por dispositivo y conservar las últimas observaciones válidas. El NaN intencional de campos no aplicables en buses debe seguir siendo distinto de un meter inválido.',
 'Inyectar NaN, +/-Infinity y valores extremos en meters, faders, tiempos y ajustes. Ninguno debe producir comandos inválidos, bloquear canales sanos o corromper el guardado.')
finding('F13','P2','Cargar un preset no aplica toda su configuración',
 'Ui/AppOrchestrator.cs:1019-1110',
 'A10: un preset con ventana de 900 ms deja el lector en 2.500 ms; un delay manual de 800 ms no envía ninguna orden; AutoLevelerEnabled=false puede coexistir con el módulo activo.',
 'La pantalla y el archivo pueden describir un estado diferente del funcionamiento real. Los delays manuales cargados no se aplican, y el siguiente reinicio puede activar un comportamiento distinto.',
 'ApplyLoadedConfig reutiliza MeterReader sin actualizar WindowMs, configura slots sin ApplyAllCurrentSettings y combina los flags anteriores con los cargados mediante OR sin actualizar config.',
 'Definir si el preset restaura el estado de módulos o conserva el actual, y reflejar la decisión en un único estado efectivo. Aplicar todas las propiedades de manera transaccional y enviar tiempos manuales después de validar los FX.',
 'Cargar dos presets contrapuestos y verificar configuración, runtime y mensajes. El guardado posterior y una recarga deben conservar exactamente el estado efectivo.')
finding('F14','P2','La reparación de JSON no cubre colecciones nulas',
 'Infrastructure/SessionConfig.cs:167-191,204-233; Ui/AppOrchestrator.cs:184-190,915-930',
 'A11: el JSON válido {"WatchedDevices":null} provoca NullReferenceException en Sanitize. Hay rutas equivalentes para DelaySlots y elementos null.',
 'Un archivo editado o parcialmente migrado no se repara como promete la documentación. En el arranque cae silenciosamente a valores por defecto; al cargar un preset muestra un error genérico.',
 'Las anotaciones nullable no validan el JSON en ejecución. Sanitize enumera listas y accede a sus elementos suponiendo que nunca son null. Tampoco completa la validación de nombres, nivel objetivo o modo BPM.',
 'Validar un DTO de entrada, normalizar nulls y devolver diagnósticos de reparación. Introducir versión de esquema y migraciones; distinguir configuración ausente, corrupta y parcialmente recuperable.',
 'Cubrir null en cada colección y elemento, nombres null, enums numéricos inválidos y rangos de objetivo. Recuperar lo válido y mostrar exactamente qué se ha corregido.')
finding('F15','P2','El guardado puede perder la sesión y anunciar éxito',
 'Ui/AppOrchestrator.cs:171-175,791-835,915-930; Program.cs:158-183',
 'Inspección: WriteSession escribe directamente sobre el destino; el cierre ignora errores de guardado y Main imprime que se ha guardado. No se ha provocado un corte de disco ni sobrescrito una sesión del usuario.',
 'Un corte durante la escritura puede truncar el único archivo. El siguiente arranque usa defaults sin aviso y una salida posterior puede sustituir la configuración que se pretendía recuperar.',
 'Falta escritura atómica con copia recuperable y falta propagación del resultado. Los catch vacíos convierten una pérdida de datos en una salida aparentemente correcta.',
 'Serializar a un temporal del mismo directorio, completar y sustituir de forma atómica según plataforma, conservando una copia anterior. Devolver un resultado de guardado, mostrar fallos y poner en cuarentena archivos corruptos antes de generar defaults.',
 'Pruebas con permisos denegados, fallo durante la escritura y JSON truncado en directorios temporales. Siempre debe quedar una versión válida o un error explícito; nunca un mensaje de éxito falso.', 'Inspección de código')
finding('F16','P2','Las ventanas de promedio de más de cinco segundos son ficticias',
 'Domain/AudioDevice.cs:43,221-231; Modules/MeterReader.cs:115-118,154-159,433-435',
 'A12: se configura una ventana de 10.000 ms, pero una muestra de hace siete segundos ya no existe. El promedio observado es -40 dB frente a unos -23 dB si se conservaran ambas muestras.',
 'Los presets admiten un ajuste que el historial no puede cumplir. El nivelador reacciona más rápido y con menos suavizado de lo que solicita el usuario.',
 'La retención es una constante de 5.000 ms, independiente del límite de 10.000 ms aceptado por MeterReader y SessionConfig.',
 'Hacer la retención coherente con la ventana máxima o rechazar configuraciones superiores a la capacidad. Preferir un buffer circular dimensionado por tiempo y frecuencia, con vencimiento basado en reloj monotónico.',
 'Verificar ventanas de 100, 2.500, 5.000 y 10.000 ms con muestras fechadas mediante un reloj de prueba. El promedio debe incluir exactamente la ventana solicitada.')
finding('F17','P2','Las reducciones pequeñas pueden no reducir nada',
 'Modules/AntiClipper.cs:241-257,669-688',
 'A13: se permite un paso de 0,1 dB sobre un head amp a 0 dB. Tras la actuación, la ganancia sigue en 0 dB, se envía un comando y se inicia el cooldown.',
 'La protección registra reducciones aparentes mientras el canal continúa saturando. El problema aparece por debajo de la resolución del control, incluso lejos de su mínimo.',
 'Se resta el paso y luego se redondea al punto más cercano con Math.Round. Un paso inferior a media unidad vuelve a la misma ganancia; el código solo contempla el caso de mínimo físico.',
 'Cuantizar en dirección de atenuación y garantizar al menos un paso físico cuando se decide actuar; alternativamente, acumular una intención pendiente explícita. Validar los parámetros frente al tipo de recurso y registrar la reducción efectiva.',
 'Pasos 0,1/0,25/0,5 dB en head amp y trim. Cada acción aceptada reduce al menos un escalón o informa con precisión de que no puede hacerlo.')
finding('F18','P2','El polling omite muestras y puede perder picos',
 'Modules/AntiClipper.cs:445-475; Modules/BpmDetector.cs:479-509; Domain/AudioDevice.cs:244-252',
 'A17: entran una muestra de clip y una silenciosa antes de la evaluación. Incluso con ConfirmationSamples=1, el anticlipping solo ve la última y no actúa.',
 'El jitter del planificador puede descartar picos ya recibidos, romper la confirmación consecutiva y perder transitorios BPM. No se trata solo de una limitación de resolución de la mesa.',
 'Los dos módulos consultan GetLatestSample cada 50 ms en lugar de consumir todas las muestras posteriores a su última secuencia procesada. Timestamp no es una cola ni un contador de recepción.',
 'Asignar secuencias y consumir todas las muestras nuevas en orden mediante un buffer o cola acotada. Mantener la confirmación de clipping según su política y procesar los flancos BPM sin bloquear el hilo receptor.',
 'Simular ráfagas de dos o más muestras por ciclo y pausas del worker. Los picos que sí llegaron deben evaluarse una vez, sin duplicados ni desbordamiento no registrado.')
finding('F19','P1','Una IPv6 aceptada puede terminar el proceso',
 'Infrastructure/SessionConfig.cs:36-39; Ui/FirstRunWizard.cs:208-225; Osc/OscClient.cs:141,186-216,295-306',
 'A21: el validador acepta ::1, pero Send lanza ArgumentException porque el socket creado es IPv4. Esa excepción no está en los catch de Send.',
 'La dirección se acepta en el asistente y en preferencias, pero el keep-alive puede terminar con una excepción no controlada en su hilo. La prueba captura la excepción para no matar el proceso de auditoría.',
 'IPAddress.TryParse admite ambas familias; new UdpClient(receivePort) crea un socket IPv4. La validación de configuración y la capacidad del transporte no coinciden.',
 'Si el producto soporta solo IPv4, validarlo y comunicarlo de forma coherente. Si se desea IPv6, construir y enlazar el socket con la familia adecuada y verificar compatibilidad del destino. Manejar errores esperados del transporte con diagnósticos.',
 'Ninguna IP aceptada debe provocar una excepción no controlada al arrancar o cambiar la conexión. Incluir IPv4, IPv6 y direcciones malformadas en pruebas sin hardware.')
finding('F20','P2','Histéresis BPM cero repite órdenes sin nueva evidencia',
 'Modules/BpmDetector.cs:293-300,519-550,709-721',
 'A15: dos taps establecen 120 BPM. Diez llamadas a TryUpdateBpm sin nuevos taps producen diez mensajes de delay idénticos cuando la histéresis vale cero, ajuste permitido por la UI.',
 'Se generan escrituras y logs continuos incluso con un tempo fijo o antiguo. Por el bucle de 50 ms, la tasa nominal puede acercarse a 20 actualizaciones por segundo y por slot.',
 'CommitNewBpm comprueba diferencia >= histéresis: con cero también pasa una diferencia cero. La rama de refinamiento vuelve a ejecutarse sin muestras nuevas y no aplica una caducidad de validez operativa.',
 'Calcular solo ante evidencia nueva; deduplicar el valor de delay efectivo y separar el umbral musical de la supresión de duplicados. Definir cuándo un BPM antiguo puede usarse para configurar nuevos slots y cuándo queda suspendido.',
 'Con histéresis cero y diez segundos sin nuevos taps, no debe haber tráfico repetido. Una variación real debe propagarse una vez; probar también pérdida de señal y cambios de canal o modo.')
finding('F21','P2','El simulador oculta la interacción entre previo y fader',
 'Osc/M32Simulator.cs:674-697,760-771; tests/M32LiveConsoleTool.Tests/SimulationLoopTests.cs:18-107',
 'A19: reducir el head amp 12 dB cambia el meter post-trim unos -12 dB, pero el post-fader varía solo -0,01 dB por el paso del tiempo.',
 'Las pruebas de integración no pueden detectar cómo compensa el nivelador una reducción del previo. El simulador puede hacer parecer independientes dos controles de la misma cadena de señal.',
 'postTrimDb incluye HeadAmpGainDb, pero postFaderDb se calcula directamente desde inputDb. También faltan expiración real de suscripciones, simulación de trim digital y registro de escrituras FX para comprobar su efecto.',
 'Modelar una cadena común de señal y añadir un reloj y fuente deterministas. Simular estados mute/trim/routing, cuantización, caducidad, pérdidas y latencias configurables. Mantener pruebas de protocolo independientes de las constantes compartidas con la implementación.',
 'Bajar el previo debe atenuar los meters posteriores. Una prueba conjunta AutoLeveler+AntiClipper debe verificar límites y ausencia de subidas de ganancia; otra debe comprobar el tiempo realmente almacenado en cada FX.')
finding('F22','P1','La tecla de pánico no funciona dentro de todos los menús',
 'Ui/ConsoleMenu.cs:83-89,112-129,161-168,1251-1258,1288-1316; Ui/AppOrchestrator.cs:385-404',
 'Inspección: solo HandleMainKey interpreta 0 como pánico. Los submenús leen sus propias teclas y AskString lo toma como texto; ShowNotice lo usa para cerrar el aviso. No se ha automatizado una terminal interactiva.',
 'En un ajuste o aviso, la misma tecla no libera el control aunque los módulos siguen ejecutándose. Además, ReleaseAll detiene los módulos secuencialmente y no cierra primero una barrera común de escritura.',
 'La captura de teclado está distribuida en múltiples lecturas bloqueantes. El pánico es una opción del menú principal, no una acción global de máxima prioridad.',
 'Centralizar la entrada y reservar una tecla inequívoca global, por ejemplo F12, disponible en formularios y avisos. Activar primero una inhibición compartida de escrituras automáticas y después detener workers; mostrar el estado de liberación.',
 'Desde cada submenú, entrada numérica, aviso y recalibración, pulsar la tecla de emergencia. Tras activar la inhibición, ningún módulo debe emitir una nueva orden automática.', 'Inspección de código')
finding('F23','P2','Las pruebas de integración no toleran una pérdida inicial',
 'tests/M32LiveConsoleTool.Tests/SimulationLoopTests.cs:47-49,91-100; Modules/MeterReader.cs:63,657-689; Modules/DelayController.cs:65,203-225',
 'Suite inicial: 105/107 superadas. Repetición aislada: 1/2 superada. FX espera Count>=4 y después accede a slots 1..4, pero los cuatro recibidos pueden ser 5..8. Un control con UdpClient puro también pierde el primer datagrama en este host.',
 'La suite falla por condiciones de transporte y por una condición de espera insuficiente. El resultado no permite atribuir por sí solo un fallo determinista al simulador.',
 'La espera de tres segundos vence antes de los reintentos de ocho segundos y no verifica las claves necesarias. Puertos fijos y temporización real añaden dependencia del entorno.',
 'Esperar las claves concretas, comprobar Connect/arranque y elegir puertos efímeros. Separar pruebas deterministas con transporte/reloj inyectables de pruebas UDP con pérdidas deliberadas y plazos acordes al contrato de recuperación.',
 'Ejecutar en Windows, Linux y macOS e inyectar pérdidas iniciales. Una pérdida debe dar recuperación comprobada o un diagnóstico útil, nunca KeyNotFoundException. No alargar todos los timeouts sin corregir la condición.')
finding('F24','P2','La cadencia real no coincide con el intervalo configurado',
 'Modules/AutoLeveler.cs:68,254-260,438-443,502-504; Domain/MeterSample.cs:57,164-166; Modules/AntiClipper.cs:599-611',
 'Inspección: se admiten ciclos de 10 ms, pero el bucle duerme en pasos de 50 ms y añade el tiempo de procesamiento. La corrección se calcula con 10 ms nominales, no con el tiempo transcurrido.',
 'En el caso de 10 ms, la velocidad efectiva queda aproximadamente en una quinta parte de la solicitada, antes de considerar carga. Los cambios del reloj del sistema también afectan al historial, cooldown y estabilización.',
 'El planificador, el cálculo de slew y las duraciones no comparten una fuente temporal monotónica. Se usa DateTime.UtcNow para intervalos de control.',
 'Introducir TimeProvider o un reloj equivalente, medir delta real monotónico y usar espera cancelable hasta el próximo vencimiento. Acotar delta tras pausas largas para evitar un salto de compensación. Reservar UTC para marcas de log.',
 'Verificar 10/25/50/100 ms con reloj simulado y bajo carga. Probar saltos de reloj civil sin alterar cooldown ni caducidad; el cambio acumulado debe respetar el límite de dB/s.', 'Inspección de código')

counts=Counter(f['priority'] for f in findings)
assert len(findings)==24 and counts=={'P1':10,'P2':14}

sections=[]
def section(title, blocks): sections.append((title, blocks))
section('Criterio general y alcance', [
 ('Conclusión', 'La base del proyecto es aprovechable: dominio separado de la red, cálculos de nivel en potencia, guardas de tamaño de blobs, validación parcial, simulador, pruebas y CI. Sin embargo, hay fallos de sincronización que pueden convertir una reducción de ganancia en una subida real o escribir sobre un control equivocado. Recomiendo resolver los P1 y validarlos con hardware antes de confiar al programa una mezcla en directo sin supervisión.'),
 ('Qué se ha revisado', 'Código de producción en Program, Domain, Infrastructure, Modules, Osc y Ui; los 15 archivos de pruebas; proyectos .NET, solución y workflows de CI/release. Se ha contrastado el comportamiento con README, la especificación de 22 páginas y los apartados de cambios/limitaciones de la entrega final. La revisión incluye arquitectura, protocolo, concurrencia, persistencia, control, UX y rendimiento.'),
 ('Límites', 'No se conectó ni se enviaron órdenes a una mesa real. Todo el tráfico de las reproducciones se limitó a loopback. No se ensayaron firmware, stage boxes, sonido, latencia acústica, largas sesiones, disco averiado ni UI interactiva. Las referencias históricas a pruebas con mesa real pertenecen a la documentación del proyecto y no son verificaciones de esta auditoría.'),
 ('Qué significa la prioridad', 'P1: corregir antes de uso en directo, por destino o sentido de las órdenes, pérdida de control o caída del proceso. P2: corregir en la siguiente iteración por exactitud, recuperación, persistencia o verificación. No se asigna P0: la auditoría no demuestra un fallo inevitable en toda ejecución. La prioridad no es una puntuación CVSS.'),
 ('Estado del repositorio', 'Base auditada: 804299018b09a32d3ecaed40e0d2c2a9012f9463, versión 1.1.0. La modificación previa de .gitignore se conserva. Se añaden informe y herramientas/evidencias de auditoría; no se corrige ni modifica código de producción. Los números de línea corresponden a esta base.')
])
section('Verificación y resultados', [
 ('Compilación y dependencias', 'dotnet build en Release: correcto, cero errores y cero advertencias. Entorno: Windows x64, SDK 10.0.302 y runtime 10.0.10. La consulta NuGet de vulnerabilidades, incluidas transitivas, no devolvió paquetes vulnerables en los proyectos revisados. Esto solo describe la respuesta del feed en esta ejecución; no certifica ausencia de vulnerabilidades ni el runtime embebido en binarios de releases anteriores.'),
 ('Suite existente', '107 pruebas ejecutadas con cobertura: 105 superadas, dos fallidas. Fallos iniciales: AutoLeveler_pulls_level_toward_target_against_the_simulator y DelayController_detects_fx_types_from_the_simulator. En una repetición aislada pasó la primera y volvió a fallar la segunda. Los TRX permiten inspeccionar las aserciones y trazas originales.'),
 ('Cobertura observada', 'Líneas: 1.239/3.795 = 32,64%; ramas: 28,70%. AntiClipper: 6,48% de líneas; AppOrchestrator: 4,65%; ConsoleMenu, ConsoleDashboard y Program: 0%. Son valores de la ejecución inicial, que contiene dos fallos; no representan cobertura combinada con las sondas. Un 100% de líneas en SessionConfig no impidió el fallo con null.'),
 ('Reproducciones independientes', 'Se conservaron 24 comprobaciones de defectos en un ejecutable separado de la solución, usando APIs reales y reflexión para pasos internos o marcas temporales. Todas registraron el comportamiento defectuoso esperado. Son sondas de diagnóstico: OBSERVED significa que el defecto sigue presente; no significa que la aplicación esté corregida. Los tests de regresión futuros deben invertir las expectativas y comprobar la conducta segura.'),
 ('Control de transporte', 'Un emisor y receptor UdpClient mínimos, sin clases del programa, recibieron 0, 1, 2, 3, 4 y 5 datagramas acumulados después de seis envíos. No se determinó la causa del descarte inicial del host ni se cambiaron sus ajustes. Las sondas de lógica precalientan y vacían su receptor local antes de medir órdenes, para no confundir pérdida del entorno con ausencia de envío.')
])
section('Rendimiento: medición y optimizaciones', [
 ('Carga local medida', f"32 canales + 16 buses, simulador, transporte y workers activos durante {profile['elapsedSeconds']:.3f} s: {profile['cpuMilliseconds']:.3f} ms de CPU, {profile['allocatedBytes']/1e6:.2f} MB asignados y {profile['receivedPackets']:,} mensajes recibidos. Se inicializaron 48 faders y 32 asociaciones de previo, y hubo datos en los 48 dispositivos. El consumo equivale aproximadamente al 3,0% de un núcleo; no al 3,0% total de cualquier ordenador."),
 ('Qué no mide esa carga', 'No incluye dibujo del dashboard, escritura a disco, fallos de red real ni ejecución prolongada. El número de mensajes incluye parámetros además de meters y depende de la fase del simulador. Hubo cero colecciones GC durante ese tramo: la asignación total no es memoria retenida ni evidencia de fuga. No se han medido percentiles de latencia ni jitter de cada orden.'),
 ('Microbenchmark de promedios', '48 dispositivos con 100 muestras cada uno y 48.000 llamadas a GetMovingAverage: 41.088.040 bytes asignados en unos 207 ms en la última ejecución. Cada llamada copia el historial. Extrapolando esa copia a 10 ciclos/s más 1 refresco/s: alrededor de 0,45 MB/s asignados; es una estimación, no una medición del dashboard.'),
 ('O1 - Historial y promedio', 'Sustituir List.RemoveAt(0) y SnapshotHistory por un buffer circular. Mantener suma de potencias y recuento al insertar/expirar, o calcular un snapshot agregado por dispositivo. Evitar una llamada al reloj por muestra. Beneficio esperado: menos copias y menos trabajo bajo lock; medir antes/después preservando exactamente la ventana y los resultados RMS.'),
 ('O2 - Transporte y asignaciones', 'Codificar con Span<byte>/BinaryPrimitives y evitar arrays temporales por escalar; parsear el blob sin otra copia cuando la vida del buffer lo permita. Cachear endpoints, no resolver el destino en cada envío. Priorizar primero validez de datos, estado observado y confirmación; usar pooling solo si las medidas justifican su complejidad.'),
 ('O3 - E/S y scheduling', 'Mover el log a una cola acotada con escritor dedicado, límites y flush configurable. Actualmente Flush por evento ocurre bajo el mismo lock que consulta la UI (ShowLogger.cs:71-93). Centralizar cadencia cancelable y deduplicar comandos efectivos. Objetivo: reducir jitter y bloquear menos, no reescribir todos los hilos por estilo.'),
 ('Cómo aceptar la optimización', 'Comparar carga de 48 dispositivos y sesiones de 30-60 minutos: CPU, bytes/s, GC, cola máxima, drops, p50/p95/p99 de recepción-decisión-envío y respuesta del pánico. Fijar un presupuesto explícito con el hardware objetivo y rechazar cualquier optimización que degrade la sincronización o cambie la política musical.')
])
section('Arquitectura propuesta y mejoras de producto', [
 ('Una autoridad de estado', 'Introducir un modelo observado de la mesa distinto de la configuración deseada. Cada recurso debe tener valor, validez, instante monotónico y generación de conexión. Head amps se identifican por recurso físico; canales y buses conservan sus asociaciones. Esto evita que cada módulo mantenga una verdad incompatible.'),
 ('Puerta única para escrituras', 'El módulo propone una intención; un coordinador comprueba pánico, conexión vigente, datos frescos, ownership del recurso, límites y cuantización antes de enviarla. Una cola acotada puede reemplazar órdenes obsoletas del mismo recurso, pero no mezclar acciones con semántica distinta. El feedback concilia estado observado y comandos pendientes.'),
 ('Ciclo de conexión', 'Desconectado -> sincronizando -> listo -> degradado. Al cambiar de IP o escena se invalida la generación afectada; al perder observaciones se suspende el control correspondiente. La UI debe mostrar por qué un módulo está esperando, no solo ON/OFF. Activar un módulo no equivale a autorizar órdenes con datos incompletos.'),
 ('Control por dispositivo', 'Separar observar, autonivelar, proteger y usar como referencia BPM. Hoy la lista WatchedDevices alimenta todos los módulos y no existe una habilitación de AutoLeveler por dispositivo. Permitir exclusiones y límites de fader/reducción acumulada por canal; es especialmente útil para claquetas, retornos y monitores.'),
 ('Operación y UX', 'Añadir edición explícita del objetivo por canal, vista de diferencias antes de aplicar presets y nombres de presets disponibles. Corregir Esc durante el nombre de un canal/bus: AddChannelFlow, AddBusFlow y AskBpmTargetChannel convierten null en el nombre por defecto y continúan (ConsoleMenu.cs:525-533,690-722). Reservar la tecla global de pánico y mantenerla visible.'),
 ('Diagnóstico y mantenibilidad', 'Exponer contadores de datagramas recibidos/rechazados, envíos fallidos, reintentos, cambios manuales y tiempos de cola. Reducir catch vacíos sin saturar el log; registrar errores con throttling y estado de recuperación. Extraer interfaces de transporte/reloj y un aplicador de configuración; no hace falta rehacer la aplicación ni sustituir su dominio.'),
 ('CI y distribución', 'Ejecutar pruebas en los tres sistemas publicados, conservar TRX/cobertura como artifacts y añadir pruebas de smoke del binario autocontenido. Fijar una política de SDK/global.json y de actualización de acciones/dependencias. La consulta NuGet no revisa por sí sola el runtime embebido de los ZIP existentes.')
])
section('Plan de corrección y pruebas de aceptación', [
 ('Fase 1 - Coherencia y control', 'Resolver F02-F08, F11, F19 y F22 con estados válidos, generación de conexión y puerta común de escritura. Corregir F01 en la misma fase para que el autonivelado básico sea fiable. Entrega verificable: pruebas deterministas de arranque incompleto, fuente digital, previo compartido, cambio de IP y pánico global; ninguna subida real causada por AntiClipper.'),
 ('Fase 2 - Exactitud y recuperación', 'Abordar F09-F10, F12-F18, F20 y F24. Aplicación de presets transaccional, escritura atómica, consumo de muestras con secuencias y reloj monotónico. Entrega verificable: ida/vuelta de presets y guardado, pérdida/reordenamiento controlados, pasos subcuánticos y ventanas completas.'),
 ('Fase 3 - Evidencia independiente', 'Corregir el modelo del simulador (F21) y los tests de integración (F23). Añadir un transporte de fallo programable y trazas OSC de referencia que no se construyan únicamente con el mismo encoder y las mismas constantes del programa. Ejecutar CI en Windows/Linux/macOS y una sesión larga con picos de carga.'),
 ('Fase 4 - Rendimiento y uso', 'Aplicar O1-O3 de forma incremental con comparación de métricas. Añadir control por dispositivo, límites y diagnósticos visibles. Actualizar README y documentación de entrega para describir el comportamiento realmente validado y las políticas elegidas.'),
 ('Pruebas mínimas antes de mesa real', 'Matriz: fuente analógica/digital/compartida; mute conocido/desconocido; fader bajo/medio/unidad; pérdida inicial/sostenida/parcial; feedback normal/duplicado/reordenado; slot delay/reverb/desconocido; preset vacío/completo/corrupto; arranque/reconexión/cambio de IP/pánico. Verificar tanto estado interno como órdenes efectivamente emitidas.'),
 ('Verificación con hardware', 'En una sesión controlada con señal de prueba y monitorización independiente, comprobar identificación de previo local y AES50, trim, enlaces estéreo/DCA/mute groups, escala de meters de buses, cuantización y ecos reales. Capturar entradas/salidas OSC y comparar con los invariantes. No extender automáticamente los resultados del simulador a un firmware no ensayado.'),
 ('Criterio de cierre', 'Cerrar cada hallazgo cuando su reproducción deje de mostrar el fallo y exista una prueba del comportamiento correcto. Exigir que todas las pruebas existentes pasen con una causa entendida para las pérdidas del entorno; no basta aumentar esperas. Documentar expresamente lo que siga pendiente de validación física.')
])
section('Documentación, supuestos y validación pendiente', [
 ('Trazabilidad con requisitos', 'CON06 exige leer fader, mute y gain antes de corregir: F02/F03/F06/F07 muestran huecos. CLI03 exige atenuación exclusivamente: F02-F04 pueden incumplirla. LEV06/LEV07 se relacionan con F01/F10/F24; CFG05 con F13; CFG02 con F14/F15. Son referencias a la intención original, no una afirmación de que toda decisión posterior de alcance deba revertirse.'),
 ('Diferencias de alcance que conviene resolver', 'LEV10 prevé habilitación por dispositivo, ausente en el modelo actual. BPM07 prevé caducidad operativa del tempo; hoy la UI lo marca antiguo, pero HasEverBeenValid permanece verdadero y pueden aplicarse tiempos desde ConfigureDelaySlot. La especificación inicial usa .NET 8 y rutas/comandos antiguos; el código y README actuales usan .NET 10. La entrega final contiene limitaciones FX que ya no describen toda la implementación actual.'),
 ('Protocolo contrastado', 'La referencia de Patrick-Gilles Maillot confirma el formato de meter blobs, /meters/6, el índice /-ha y la ganancia de head amp, así como la ubicación de los tiempos de delay. No se encontró motivo para sustituir el mapeo principal de esos comandos. La referencia es comunitaria y primaria respecto a su investigación, no una certificación del fabricante.'),
 ('Punto concreto para comprobar', 'SubscribeBusPage envía /batchsubscribe con argumentos 0 y 48. El texto del protocolo indica cero en los argumentos de meter que no se usan. El simulador ignora esa diferencia. Verificar con captura si el firmware la tolera y normalizar a 0/0 si procede; se registra como discrepancia documental, no como fallo demostrado en hardware.'),
 ('Otras incertidumbres relevantes', 'Determinar si el firmware emite /-ha/index al cambiar routing; qué meters reflejan DCA/mute groups; cómo se comportan previos compartidos entre consolas; y cómo se reflejan faders enlazados. El simulador no resuelve estas cuestiones. La política musical frente a atenuación de previo, fader de compensación y buses anidados debe definirse y probarse con señales representativas.'),
 ('Límites de una protección por meters', 'El anticlipping es una corrección reactiva de ganancia, no un limitador de audio que pueda impedir todos los picos. El periodo de meters y la confirmación añaden latencia. Conviene precisar ese alcance en el producto, medir el tiempo efectivo de respuesta y mantener controles de ganancia conservadores. Esto no sustituye la corrección de picos omitidos por software en F18.')
])
section('Evidencias, reproducción y fuentes', [
 ('Informe y herramientas', 'output/auditoria-m32-2026-10-16.markdown y output/pdf/auditoria-m32-2026-10-16.html contienen el informe histórico. tests/audit-2026-10-16/Program.cs contiene las sondas; AuditProbe.csproj es independiente y no se ha incorporado a la solución. build_report.py genera los dos formatos a partir de los hallazgos y las mediciones conservadas.'),
 ('Resultados conservados', 'tests/audit-2026-10-16/evidence/probes.json, load-profile.json, dependencies.json, baseline.trx, simulation-repeat.trx y coverage.cobertura.xml. probe-output.txt guarda la salida legible de las reproducciones. La consulta de vulnerabilidades no devolvió entradas vulnerables; se conserva la respuesta original.'),
 ('Reproducir sondas', 'Desde la raíz: dotnet run --project tests/audit-2026-10-16/AuditProbe.csproj -c Release. Para el perfil local: añadir -- --load. Estas herramientas escriben resultados en evidence y solo se comunican por loopback. Usan reflexión; tras un refactor puede ser necesario adaptar los nombres internos. Revisar cada resultado y no interpretar el código de salida 0 como ausencia de defectos.'),
 ('Reproducir suite', 'dotnet test M32LiveConsoleTool.slnx -c Release --collect:"XPlat Code Coverage" --results-directory tests/TestResults/audit-repeat. Para los dos tests UDP: añadir --filter FullyQualifiedName~SimulationLoopTests. La pérdida inicial observada depende del host; no se han alterado el firewall, puertos de la mesa ni ajustes de red.'),
 ('Fuentes locales', 'Código y tests del commit indicado; README.md; Docs/Cristian_Moresi_Nadal_Specs_Proyecto.pdf; Docs/Entrega Final/informe.html; Docs/X32-OSC.pdf. La documentación histórica se usa como contexto y trazabilidad, con precedencia del comportamiento implementado al describir defectos actuales.'),
 ('Referencia externa consultada', 'Patrick-Gilles Maillot, Unofficial X32/M32 OSC Remote Protocol, PDF enlazado desde su web; apartados de meters (pp. 16-18), headamp (p. 43), suscripciones (pp. 78-79) y tablas de efectos. Consulta: 16 de octubre de 2026. Enlaces completos a continuación.')
])
sources=[
 ('Página del autor y acceso al protocolo','https://sites.google.com/site/patrickmaillot/x32'),
 ('PDF del protocolo consultado','https://drive.google.com/file/d/1Yt_S1mpPt3CAzeq3Dnpe_IqctQ-1GlTz/view'),
 ('Repositorio de referencia del autor','https://github.com/pmaillot/X32-Behringer'),
]

# HTML: complete searchable report, local and self contained.
css='''
:root{--ink:#16263a;--muted:#56677c;--accent:#116e87;--paper:#fff;--bg:#edf1f5;--line:#d6e0e9}
*{box-sizing:border-box}body{font:16px/1.58 "Segoe UI",Arial,sans-serif;margin:0;color:var(--ink);background:var(--bg)}
main{max-width:1060px;margin:34px auto;background:white;padding:54px 66px;box-shadow:0 14px 48px #16263a13}
h1{font-size:44px;line-height:1.1;margin:16px 0 22px}h2{font-size:27px;margin:40px 0 16px;color:var(--accent)}h3{font-size:21px;line-height:1.3}p{margin:10px 0 16px}.eyebrow{font-size:12px;text-transform:uppercase;letter-spacing:2px;color:var(--accent);font-weight:700}
.sub{color:var(--muted);font-size:19px}.metrics{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin:28px 0}.metric{background:#eff6f8;padding:20px;border-top:3px solid var(--accent)}.metric b{display:block;font-size:30px}.metric span{font-size:13px}
.note{border-left:4px solid #c77328;background:#fff7ed;padding:18px 22px;margin:24px 0}.meta,.refs{font-size:13px;color:var(--muted)}.refs{font-family:Consolas,monospace;overflow-wrap:anywhere}
table{width:100%;border-collapse:collapse;margin:20px 0;font-size:14px}th,td{padding:10px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}th{background:#e9f0f5}a{color:#086f8d}.finding{border:1px solid var(--line);border-radius:7px;padding:24px;margin:22px 0;scroll-margin-top:25px}.finding h3{margin:0 0 10px}.pill{display:inline-block;font-size:12px;padding:3px 8px;border-radius:4px;background:#fff1e8;color:#873c0d;vertical-align:middle}input{width:100%;padding:13px;font:inherit;border:1px solid #9eb3c4;border-radius:5px}nav a{display:inline-block;margin:3px 12px 3px 0}.block b{color:#223f59}.footer{border-top:1px solid var(--line);margin-top:40px;padding-top:20px;color:var(--muted);font-size:13px}
@media(max-width:760px){main{padding:28px 22px;margin:0}.metrics{grid-template-columns:repeat(2,1fr)}h1{font-size:34px}}
@media print{body{background:white}main{box-shadow:none;padding:0;margin:0;max-width:none}input,.search{display:none}.finding{break-inside:avoid}h2{break-before:auto}.metrics{grid-template-columns:repeat(4,1fr)}}
'''
esc=html.escape
parts=['<!doctype html><html lang="es"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Auditoría técnica - M32 Live Console Tool</title><style>'+css+'</style><main>',
 '<div class="eyebrow">Auditoría de software · 16 octubre 2026</div><h1>M32 Live<br>Console Tool</h1><p class="sub">Errores, soluciones y plan de mejora<br>Versión 1.1.0 · revisión 8042990</p>',
 '<div class="metrics"><div class="metric"><b>24</b><span>hallazgos priorizados</span></div><div class="metric"><b>10</b><span>P1: antes de directo</span></div><div class="metric"><b>105/107</b><span>pruebas iniciales superadas</span></div><div class="metric"><b>32,64%</b><span>cobertura de líneas observada</span></div></div>',
 '<div class="note"><b>Prioridad: corregir la coherencia del control.</b> La carga local medida es moderada, pero se han reproducido decisiones con estado incompleto y ganancias desactualizadas. La validación física sigue pendiente.</div>',
 '<nav><a href="#alcance">Alcance</a><a href="#hallazgos">Hallazgos</a><a href="#rendimiento">Rendimiento</a><a href="#arquitectura">Mejoras</a><a href="#plan">Plan</a><a href="#fuentes">Evidencias</a></nav>']
def html_section(title,blocks,id):
    return '<section id="'+id+'"><h2>'+esc(title)+'</h2>'+''.join('<p class="block"><b>'+esc(h)+'.</b> '+esc(t)+(' <a href="https://drive.google.com/file/d/1Yt_S1mpPt3CAzeq3Dnpe_IqctQ-1GlTz/view">Referencia de protocolo</a>.' if h in ('Protocolo contrastado','Punto concreto para comprobar') else '')+'</p>' for h,t in blocks)+'</section>'
parts.extend([html_section(*sections[0],'alcance'),html_section(*sections[1],'verificacion')])
parts.append('<section id="hallazgos"><h2>Registro de hallazgos</h2><p>10 P1 y 14 P2. La ficha de cada hallazgo incluye evidencia, causa, solución propuesta y criterio de aceptación.</p><input id="filter" aria-label="Filtrar hallazgos" placeholder="Buscar por ID, prioridad, módulo o texto…"><table><thead><tr><th>ID</th><th>Prioridad</th><th>Hallazgo</th><th>Evidencia</th></tr></thead><tbody>')
for f in findings:
    parts.append(f'<tr><td><a href="#{f["id"]}">{f["id"]}</a></td><td>{f["priority"]}</td><td>{esc(f["title"])}</td><td>{esc(f["status"])}</td></tr>')
parts.append('</tbody></table>')
for f in findings:
    parts.append(f'<article class="finding" id="{f["id"]}"><h3>{f["id"]} <span class="pill">{f["priority"]}</span> {esc(f["title"])}</h3><p class="refs">{esc(f["refs"])}</p>')
    for label,key in [('Evidencia','evidence'),('Impacto','impact'),('Causa','cause'),('Solución propuesta','fix'),('Prueba de aceptación','acceptance')]:
        parts.append(f'<p><b>{label}.</b> {esc(f[key])}</p>')
    parts.append('</article>')
parts.append('</section>')
for (title,blocks),id in zip(sections[2:],['rendimiento','arquitectura','plan','documentacion','fuentes']): parts.append(html_section(title,blocks,id))
parts.append('<ul>'+''.join(f'<li><a href="{u}">{esc(t)}</a></li>' for t,u in sources)+'</ul>')
parts.append('<p class="footer">Informe de auditoría, no certificación de hardware. Se conserva el código de producción y el cambio previo de .gitignore. Las sondas OBSERVED documentan defectos presentes.</p></main><script>document.getElementById("filter").addEventListener("input",e=>{let q=e.target.value.toLocaleLowerCase();document.querySelectorAll(".finding").forEach(x=>x.hidden=!x.textContent.toLocaleLowerCase().includes(q))});</script></html>')
(OUT/'auditoria-m32-2026-10-16.html').write_text('\n'.join(parts),encoding='utf-8')

# Markdown is the maintained report format requested by the project owner.
lines = ['# Auditoría técnica — M32 Live Console Tool', '', '**Base auditada:** 8042990 · versión 1.1.0 · 16 de octubre de 2026.', '', '24 hallazgos: 10 P1 y 14 P2. Este documento conserva los resultados anteriores a las correcciones.', '']
def md_section(title, blocks):
    lines.extend(['## ' + title, ''])
    for label, text in blocks:
        lines.extend(['### ' + label, '', text, ''])
for title, blocks in sections[:2]: md_section(title, blocks)
lines.extend(['## Hallazgos detallados', ''])
for f in findings:
    lines.extend([f'### {f["id"]} · {f["priority"]} · {f["title"]}', '', '**Referencias:** ' + f['refs'], '', '**Validación:** ' + f['status'], ''])
    for label, key in [('Evidencia', 'evidence'), ('Impacto', 'impact'), ('Causa', 'cause'), ('Solución propuesta', 'fix'), ('Prueba de aceptación', 'acceptance')]:
        lines.extend(['**' + label + '.** ' + f[key], ''])
for title, blocks in sections[2:]: md_section(title, blocks)
for title, url in sources: lines.extend([f'- [{title}]({url})'])
(ROOT/'output/auditoria-m32-2026-10-16.markdown').write_text('\n'.join(lines) + '\n', encoding='utf-8')
(EVIDENCE/'findings.json').write_text(json.dumps(findings,ensure_ascii=False,indent=2),encoding='utf-8')
print('Generated Markdown and HTML. Findings:',len(findings),dict(counts))
