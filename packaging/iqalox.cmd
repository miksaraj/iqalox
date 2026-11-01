@echo off
rem One-command runner shipped in the release bundle: iqalox program.iqx
"%~dp0iqaloxc.exe" run %*
exit /b %errorlevel%
