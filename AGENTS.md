# Chat formatting

- LaTeX does not render in chat. Write math in Markdown fenced code blocks
  using plain-text notation instead of LaTeX.
- Prefer short math variable names, and define them when needed.

# Shader behavior

- Every shader scene must eventually render black without music, including after
  previously active audio. Trails, history and fluid may decay naturally, but
  ambient lighting, procedural backgrounds and persistent simulation state must
  not keep the screen lit indefinitely.
- Audio-independent proof-of-concept/prototype scenes, such as `MandelVolume` and `MandelLight`,
  are exceptions to this rule. Mark and document them as prototypes; apply the
  silence requirement when they become production audio-reactive scenes.
- Preserve this invariant with the GPU regression in
  `AEther.Tests/ShaderSilenceTests.cs`. Allow a generous decay period before
  applying strict checks to every RGB channel across multiple final frames.

# Performance measurements

- Run rendering performance timing tests at a minimum resolution of 1280x720
  (720p). Use 720p or higher for reported performance comparisons.
