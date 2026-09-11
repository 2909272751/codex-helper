# dsh-model-vision-bridge

This DSH Web profile plugin fixes a client/runtime metadata spelling mismatch:
some DSH surfaces inspect `input`, while runtime model resolution returns
`inputModalities`. It mirrors one existing field to the other for JSON API
responses, without adding, removing, or guessing any modality.

Therefore a model becomes image-capable in the UI only when its resolved DSH
metadata already includes `image`. Text-only models remain text-only. Disable
the bundle in DSH Plugin Manager and restart DSH to revert.
