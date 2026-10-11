@echo off
title ORBITAL TRACE - 01
color 0A
for /L %%N in (1,1,140) do @echo [ORBITAL TRACE %%N] SAMPLE=%RANDOM%%RANDOM% ROUTE=UNRESOLVED
timeout /t 2 /nobreak >nul
exit
