@echo off
title CLOCK DRIFT - 04
color 0D
for /L %%N in (1,1,140) do @echo [CLOCK DRIFT %%N] TICK=%RANDOM%%RANDOM% EPOCH=DESYNC
timeout /t 2 /nobreak >nul
exit
