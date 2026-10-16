# Shader behavior

- Every shader scene must eventually render black without music, including after
  previously active audio. Trails, history and fluid may decay naturally, but
  ambient lighting, procedural backgrounds and persistent simulation state must
  not keep the screen lit indefinitely.
- Preserve this invariant with the GPU regression in
  `AEther.Tests/ShaderSilenceTests.cs`. Allow a generous decay period before
  applying strict checks to every RGB channel across multiple final frames.
