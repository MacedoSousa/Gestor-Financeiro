# ADR-009 — Comprovantes lidos em casa; Gemini pago só com agregados

**Decisão:** comprovantes passam por: texto do PDF → QR/código de barras → OCR → modelo de visão local (Qwen-VL 8–9B em Q4 no Ollama, RTX 2060 Super). O Gemini (tier pago) recebe só números agregados com apelidos e um esquema JSON de saída; cada número citado é conferido contra o FinanceEngine.

**Por quê:** no tier gratuito o Google usa os dados para treinar modelos. LLM erra conta; o cálculo é do código.

**Descartado:** Gemini gratuito; mandar imagens de comprovantes para a nuvem.
