# OCR models

Not committed — download the RapidOCR ONNX exports and drop them here.

| File | Role |
|---|---|
| `ch_PP-OCRv4_det_infer.onnx` | detection, fp32 |
| `en_PP-OCRv4_rec_infer.onnx` | recognition, fp32 |
| `en_dict.txt` | recognition dictionary |
| `ch_PP-OCRv4_det_infer_quant.onnx` | detection, int8 — laptop profile |
| `en_PP-OCRv4_rec_infer_quant.onnx` | recognition, int8 — laptop profile |

`"ocr.quantized": true` (which the `laptop` profile sets by default) switches to
the `_quant` pair. The dictionary is shared by both.

The detector is language-independent — it looks for rectangles that contain
text — so the Chinese-trained `ch_` detector is the right one to pair with the
English recognizer. The angle classifier (`cls`) is deliberately not used: the
game UI is horizontal and it would cost 3–5 ms per line for nothing.

`en_dict.txt` must be the file that shipped with the recognition model. The
decoder prepends the CTC blank and appends a space, so a dictionary from a
different model produces plausible-looking garbage rather than an error.
