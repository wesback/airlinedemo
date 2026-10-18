#!/bin/sh
exec dotnet run --no-build --project backend/DemoCaseGenerator -- capture-timing
