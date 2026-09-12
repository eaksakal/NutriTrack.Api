#!/usr/bin/env bash
# Handprobe fuer die Gemini-Anbindung (Plan Task 2, Schritt 1 / Task 9, Schritt 5).
#
# Warum dieses Skript existiert: die Tests sprechen nie mit Google (StubGeminiHandler). Sie
# beweisen deshalb nur, dass Code und Stub dieselbe Huelle annehmen — nicht, dass die Huelle
# stimmt. Diese eine Probe ist der einzige Weg, den Vertrag mit dem echten Dienst zu pruefen;
# sie gehoert vor jeden Deploy, der die Gemini-Anbindung anfasst.
#
# Aufruf:
#   NUTRITRACK_GEMINI_KEY=... scripts/gemini-probe.sh [modellname]
#
# Erwartet wird ein Umschlag, in dem der JSON-Text unter steps[].content[].text steckt
# (Schritt mit type == "model_output") oder in output_text auf der Wurzel. Genau diese beiden
# Pfade liest GeminiService.ExtractPayload. Weicht die Ausgabe ab, ist ExtractPayload samt
# StubGeminiHandler.Payload nachzuziehen — und zwar nach der Ausgabe hier, nicht nach Vermutung.

set -euo pipefail

: "${NUTRITRACK_GEMINI_KEY:?NUTRITRACK_GEMINI_KEY ist nicht gesetzt}"

MODEL="${1:-${NUTRITRACK_GEMINI_MODEL:-gemini-3.5-flash}}"
API_REVISION="2026-05-20"

response=$(curl -sS -X POST "https://generativelanguage.googleapis.com/v1beta/interactions" \
  -H "x-goog-api-key: ${NUTRITRACK_GEMINI_KEY}" \
  -H "Api-Revision: ${API_REVISION}" \
  -H 'Content-Type: application/json' \
  -d "$(cat <<JSON
{
  "model": "${MODEL}",
  "input": "Nutzer: zwei Broetchen mit Gouda\n",
  "system_instruction": "Du zerlegst deutschsprachige Beschreibungen von Mahlzeiten in einzelne Posten. estimate sind Naehrwerte je 100 g; sodium in GRAMM je 100 g, nicht in Milligramm. Antworte ausschliesslich im vorgegebenen Schema.",
  "response_format": {
    "type": "text",
    "mime_type": "application/json",
    "schema": {
      "type": "object",
      "properties": {
        "question": { "type": "string" },
        "items": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "searchTerm": { "type": "string" },
              "label": { "type": "string" },
              "quantityInGrams": { "type": "number" },
              "mealType": { "type": "string" },
              "estimate": {
                "type": "object",
                "properties": {
                  "calories": { "type": "number", "description": "Kilokalorien (kcal) je 100 g" },
                  "protein": { "type": "number", "description": "Eiweiss in Gramm je 100 g" },
                  "carbohydrates": { "type": "number", "description": "Kohlenhydrate in Gramm je 100 g" },
                  "fat": { "type": "number", "description": "Fett in Gramm je 100 g" },
                  "sodium": { "type": "number", "description": "Natrium in GRAMM je 100 g, nicht in Milligramm" }
                },
                "required": ["calories", "protein", "carbohydrates", "fat"]
              }
            },
            "required": ["searchTerm", "label", "quantityInGrams", "mealType", "estimate"]
          }
        }
      },
      "required": ["items"]
    }
  }
}
JSON
)")

echo "--- Rohe Antwort -------------------------------------------------------"
echo "${response}"

echo
echo "--- Pruefung des Feldpfades --------------------------------------------"
if command -v jq >/dev/null 2>&1; then
  text=$(echo "${response}" \
    | jq -r 'if .output_text then .output_text
             else ([.steps[]? | select(.type == "model_output") | .content[]? | .text? // empty] | last)
             end // empty')

  if [ -z "${text}" ]; then
    echo "FEHLER: weder output_text noch steps[].content[].text gefunden." >&2
    echo "Wurzelfelder: $(echo "${response}" | jq -r 'keys | join(", ")')" >&2
    echo "GeminiService.ExtractPayload und StubGeminiHandler.Payload muessen nachgezogen werden." >&2
    exit 1
  fi

  echo "OK: Nutzlast gefunden. Inhalt:"
  echo "${text}" | jq .

  echo
  echo "Natrium-Werte (muessen Gramm je 100 g sein, also < 40 - nicht 450):"
  echo "${text}" | jq -r '.items[]? | "  \(.label): \(.estimate.sodium // "-")"'
else
  echo "jq nicht installiert - bitte von Hand pruefen, ob der Text unter"
  echo "steps[].content[].text (type == \"model_output\") oder in output_text steht."
fi
