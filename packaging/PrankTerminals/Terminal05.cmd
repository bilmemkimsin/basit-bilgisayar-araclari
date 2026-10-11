@echo off
title NODE INDEX - 05
color 0E
for /L %%N in (1,1,140) do @echo [NODE INDEX %%N] NODE=%RANDOM% EDGE=%RANDOM% GRAPH=AMBIGUOUS
timeout /t 2 /nobreak >nul
exit
