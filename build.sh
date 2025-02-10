#!/bin/bash
set -e

cd ImpunityCodeGenerator
./build.sh
cd ..

cd ImpunityRuntime
./build.sh
cd ..

cd ImpunityStandaloneServer
./build.sh
cd ..

