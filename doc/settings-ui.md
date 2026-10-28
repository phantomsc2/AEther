# Settings UI

The settings hub keeps `AppSettings.Analyzer` in the left column and
`AppSettings.Graphics` in the right column. Both use `OptionsEditor<T>`.
Properties are selected through `TypeDescriptor`: writable, browsable bools
become toggles, enums become dropdowns, and ints/doubles become numeric editors
with linear sliders.

`Category` groups controls, `DisplayName` supplies labels, `Description` supplies
tooltips, and `System.ComponentModel.DataAnnotations.Range` supplies fixed
numeric bounds. Defaults come from the option instance's initializers. There
are no custom slider annotations or property-name cases in the editor.

The controls expose each property's actual numeric value. Four analyzer
properties perform nonlinear conversions themselves:

| Editable property | Physical setting |
| --- | --- |
| `SwiftWidthOctaves` | `SwiftWidthScale = 2^x` |
| `LogAccelerationControl` | `LogAccelerationIntensity = 1e-8 * (2^x - 1)` |
| `ComplexAccelerationControl` | `ComplexAccelerationIntensity = 1e-8 * (2^x - 1)` |
| `SmoothingNoiseLog2` | `SmoothingNoiseFloor = 2^x` |

These editable properties are ignored by JSON. Their physical settings remain
persisted but are hidden from the generated UI. Labels and descriptions state
the editable units. Processing continues to consume physical values.

Relationships between values, such as frequency ordering, splitter support,
and Nyquist limits, are checked by `AnalyzerOptions.Validate` when applying
settings. The UI does not dynamically change bounds or disable controls by
backend; the selected backend consumes its applicable values.

Input and scene selection and shader parameters remain separate runtime
controls outside `AppSettings`. Shader parameter editors retain their existing
shader-reflection implementation.
