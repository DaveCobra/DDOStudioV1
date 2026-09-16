@echo off
setlocal
cd /d "%~dp0"

if exist "src\DDOStudio.Electron" rmdir /s /q "src\DDOStudio.Electron"
if exist "ATTACHMENT_HOOK_INSPECTOR.md" del /q "ATTACHMENT_HOOK_INSPECTOR.md"
if exist "RUNTIME_EFFECT_INVESTIGATOR.md" del /q "RUNTIME_EFFECT_INVESTIGATOR.md"
if exist "src\DDOAssetStudio\viewer\anim_05000051.decoded.json" del /q "src\DDOAssetStudio\viewer\anim_05000051.decoded.json"

rem 1.7.2: environment artwork is user-supplied only.
if exist "src\DDOAssetStudio\viewer\environments" rmdir /s /q "src\DDOAssetStudio\viewer\environments"

rem Remove old generated release output so obsolete bundled environments cannot survive an upgrade build.
if exist "release\preview\DDO Studio\viewer\environments" rmdir /s /q "release\preview\DDO Studio\viewer\environments"
if exist "release\portable\DDO Studio\viewer\environments" rmdir /s /q "release\portable\DDO Studio\viewer\environments"


rem 1.7.2 documentation supersedes the 1.5.2 packaged manual.
if exist "docs\DDO_Studio_Technical_Documentation_1.5.2.md" del /q "docs\DDO_Studio_Technical_Documentation_1.5.2.md"
if exist "docs\DDO_Studio_Technical_Documentation_1.5.2.pdf" del /q "docs\DDO_Studio_Technical_Documentation_1.5.2.pdf"
if exist "docs\DDO_Studio_Technical_Documentation_1.5.3.md" del /q "docs\DDO_Studio_Technical_Documentation_1.5.3.md"
if exist "docs\DDO_Studio_Technical_Documentation_1.5.4.md" del /q "docs\DDO_Studio_Technical_Documentation_1.5.4.md"
if exist "docs\DDO_Studio_Technical_Documentation_1.5.5.md" del /q "docs\DDO_Studio_Technical_Documentation_1.5.5.md"
if exist "docs\DDO_Studio_Technical_Documentation_1.5.6.md" del /q "docs\DDO_Studio_Technical_Documentation_1.5.6.md"
if exist "docs\DDO_Studio_Technical_Documentation_1.5.7.md" del /q "docs\DDO_Studio_Technical_Documentation_1.5.7.md"
if exist "docs\DDO_Studio_Technical_Documentation_1.5.8.md" del /q "docs\DDO_Studio_Technical_Documentation_1.5.8.md"
if exist "docs\DDO_Studio_Technical_Documentation_1.5.9.md" del /q "docs\DDO_Studio_Technical_Documentation_1.5.9.md"
if exist "UPDATE_1.5.5.txt" del /q "UPDATE_1.5.5.txt"
if exist "UPDATE_1.5.6.txt" del /q "UPDATE_1.5.6.txt"
if exist "UPDATE_1.5.7.txt" del /q "UPDATE_1.5.7.txt"
if exist "UPDATE_1.5.8.txt" del /q "UPDATE_1.5.8.txt"
if exist "UPDATE_1.5.9.txt" del /q "UPDATE_1.5.9.txt"
if exist "UPDATE_1.6.0.txt" del /q "UPDATE_1.6.0.txt"
if exist "BUILD_HOTFIX_1.6.0.txt" del /q "BUILD_HOTFIX_1.6.0.txt"
if exist "PREVIEW_BUILDER_HOTFIX.txt" del /q "PREVIEW_BUILDER_HOTFIX.txt"
if exist "docs\DDO_Studio_Technical_Documentation_1.6.0.md" del /q "docs\DDO_Studio_Technical_Documentation_1.6.0.md"
if exist "docs\DDO_Studio_Technical_Documentation_1.6.1.md" del /q "docs\DDO_Studio_Technical_Documentation_1.6.1.md"
if exist "UPDATE_1.6.1.txt" del /q "UPDATE_1.6.1.txt"
if exist "UPDATE_1.6.1_PREVIEW_CLEANUP_HOTFIX.txt" del /q "UPDATE_1.6.1_PREVIEW_CLEANUP_HOTFIX.txt"
if exist "docs\DDO_Studio_Technical_Documentation_1.6.2.md" del /q "docs\DDO_Studio_Technical_Documentation_1.6.2.md"
if exist "UPDATE_1.6.2.txt" del /q "UPDATE_1.6.2.txt"
if exist "docs\DDO_Studio_Technical_Documentation_1.6.3.md" del /q "docs\DDO_Studio_Technical_Documentation_1.6.3.md"
if exist "UPDATE_1.6.3.txt" del /q "UPDATE_1.6.3.txt"
if exist "docs\DDO_Studio_Technical_Documentation_1.6.4.md" del /q "docs\DDO_Studio_Technical_Documentation_1.6.4.md"
if exist "UPDATE_1.6.4.txt" del /q "UPDATE_1.6.4.txt"
if exist "docs\DDO_Studio_Technical_Documentation_1.6.5.md" del /q "docs\DDO_Studio_Technical_Documentation_1.6.5.md"
if exist "UPDATE_1.6.5.txt" del /q "UPDATE_1.6.5.txt"
if exist "docs\DDO_Studio_Technical_Documentation_1.6.6.md" del /q "docs\DDO_Studio_Technical_Documentation_1.6.6.md"
if exist "UPDATE_1.6.6.txt" del /q "UPDATE_1.6.6.txt"
if exist "docs\DDO_Studio_Technical_Documentation_1.6.7.md" del /q "docs\DDO_Studio_Technical_Documentation_1.6.7.md"
if exist "UPDATE_1.6.7.txt" del /q "UPDATE_1.6.7.txt"
if exist "APPLY_UPDATE_1.6.7.txt" del /q "APPLY_UPDATE_1.6.7.txt"
if exist "docs\DDO_Studio_Technical_Documentation_1.6.8.md" del /q "docs\DDO_Studio_Technical_Documentation_1.6.8.md"
if exist "UPDATE_1.6.8.txt" del /q "UPDATE_1.6.8.txt"
if exist "APPLY_UPDATE_1.6.8.txt" del /q "APPLY_UPDATE_1.6.8.txt"

rem Remove superseded 1.6.9 notes and investigation files from an upgraded source tree.
if exist "docs\DDO_Studio_Technical_Documentation_1.6.9.md" del /q "docs\DDO_Studio_Technical_Documentation_1.6.9.md"
if exist "UPDATE_1.6.9.txt" del /q "UPDATE_1.6.9.txt"
if exist "FINAL_BUILD_NOTES.txt" del /q "FINAL_BUILD_NOTES.txt"
if exist "REMOVED_FILES.txt" del /q "REMOVED_FILES.txt"
if exist "NPC_APPEARANCE_NOTES.md" del /q "NPC_APPEARANCE_NOTES.md"
if exist "ANIMATION_BINDING_INSPECTOR.md" del /q "ANIMATION_BINDING_INSPECTOR.md"
if exist "ANIMATION_DECODER_TEST.md" del /q "ANIMATION_DECODER_TEST.md"
if exist "ANIMATION_SDK_INTROSPECTION.md" del /q "ANIMATION_SDK_INTROSPECTION.md"
if exist "HAVOK_DECODER_NOTES.md" del /q "HAVOK_DECODER_NOTES.md"


rem Remove superseded 1.7.0 update notes when upgrading.
if exist "UPDATE_1.7.0.txt" del /q "UPDATE_1.7.0.txt"
if exist "APPLY_UPDATE_1.7.0.txt" del /q "APPLY_UPDATE_1.7.0.txt"


rem Remove superseded 1.7.1 notes/documentation when upgrading to the final 1.7.2 source.
if exist "UPDATE_1.7.1.txt" del /q "UPDATE_1.7.1.txt"
if exist "APPLY_UPDATE_1.7.1.txt" del /q "APPLY_UPDATE_1.7.1.txt"
if exist "docs\DDO_Studio_Program_Overview_1.7.1.pdf" del /q "docs\DDO_Studio_Program_Overview_1.7.1.pdf"
if exist "docs\DDO_Studio_Technical_Documentation_1.7.1.pdf" del /q "docs\DDO_Studio_Technical_Documentation_1.7.1.pdf"
if exist "docs\DDO_Studio_Technical_Documentation_1.7.1.docx" del /q "docs\DDO_Studio_Technical_Documentation_1.7.1.docx"

echo DDO Studio 1.7.2 obsolete source/release files removed.
exit /b 0
