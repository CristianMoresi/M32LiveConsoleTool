# Evidencias históricas de auditoría

`evidence/` y `probe-output.txt` documentan la revisión `804299018b09a32d3ecaed40e0d2c2a9012f9463`, antes de corregir los defectos. No describen el estado actual del programa.

Las sondas históricas de `Program.cs` acceden por reflexión a miembros de aquella implementación. Para reproducirlas hay que usar una copia separada de esa revisión con esta carpeta de herramientas, y ejecutar con `--historical-probes`. El perfil `--load` también pertenece a esa base. Ambos escriben en `evidence/`; no deben ejecutarse para validar el producto corregido ni sobrescribir las evidencias conservadas.

La suite vigente está en `../M32LiveConsoleTool.Tests/`. Desde la raíz:

```sh
dotnet test M32LiveConsoleTool.slnx -c Release
dotnet run -c Release -- --self-test 12
dotnet run -c Release -- --self-test 1800
```

El microbenchmark vigente conserva la carga de comparación: 48 dispositivos, 100 muestras por dispositivo y 48.000 consultas de promedio. Es independiente de las sondas de defectos:

```sh
dotnet run --project tests/audit-2026-10-16/AuditProbe.csproj -c Release -- --benchmark
```

`build_report.py` genera Markdown y HTML del informe histórico con las evidencias conservadas. No genera PDF. Las correcciones y nuevas mediciones se documentan en [el registro de correcciones](../../output/correcciones-m32-2026-10-16.markdown).
