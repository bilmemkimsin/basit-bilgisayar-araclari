@echo off
title MEMORY MAP - 03
color 0B
for /L %%N in (1,1,140) do @echo [MEMORY MAP %%N] PAGE=%RANDOM% OFFSET=%RANDOM% ALIGNMENT=UNSET
timeout /t 2 /nobreak >nul
exit
