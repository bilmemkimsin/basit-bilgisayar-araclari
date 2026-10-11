@echo off
title AUTH CONTEXT - 02
color 0C
for /L %%N in (1,1,140) do @echo [AUTH CONTEXT %%N] TOKEN=%RANDOM%%RANDOM% QUORUM=UNKNOWN
timeout /t 2 /nobreak >nul
exit
