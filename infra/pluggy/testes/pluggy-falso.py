#!/usr/bin/env python3
"""Pluggy falsa para os testes do medir-saldos.sh: só biblioteca padrão, só dados sintéticos.

Uso: pluggy-falso.py <arquivo-da-porta> <arquivo-de-registro>
Escuta em 127.0.0.1 numa porta livre, grava a porta em <arquivo-da-porta> e registra cada
requisição ("METODO caminho") em <arquivo-de-registro>. Aceita só o clientId/clientSecret
sintéticos abaixo e só a apiKey que ele mesmo emitiu.

Dados (hoje = 2026-11-11 no teste):
- cartão "c1": valor usado (balance) 1500.00 = confirmadas da fatura aberta (1000.00)
  + pendente (200.00) + parcela futura (300.00) → fórmula (c). A fatura fechada "b1" e o
  pagamento dela não entram. Traz um código de banco sintético em institution.code.
- corrente "k1": saldo 12345.67, uma transação "Mercado Sintético", uma de valor zero e uma
  sem descrição; nenhuma pendente. Transações em duas páginas (cursor "after").
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

CLIENT_ID = "id-sintetico"
CLIENT_SECRET = "segredo-sintetico"
API_KEY = "chave-temporaria-sintetica"
ITEM = "item-sintetico"


def tx(id_, data, valor, tipo, status="POSTED", descricao="Compra sintética", bill=None,
       categoria=("01000000", "Groceries"), parcela=None):
    t = {"id": id_, "date": data, "amount": valor, "type": tipo, "status": status,
         "description": descricao, "categoryId": categoria[0], "category": categoria[1],
         "creditCardMetadata": None}
    if bill or parcela:
        t["creditCardMetadata"] = {"billId": bill}
        if parcela:
            t["creditCardMetadata"].update({"installmentNumber": parcela[0], "totalInstallments": parcela[1]})
    return t


CONTAS = [
    {"id": "k1", "type": "BANK", "subtype": "CHECKING_ACCOUNT", "name": "Conta Sintética",
     "number": "99999-9", "balance": 12345.67, "bankData": {"transferNumber": "0001/99999-9"}},
    {"id": "c1", "type": "CREDIT", "subtype": "CREDIT_CARD", "name": "Cartão Sintético",
     "number": "4321", "balance": 1500.00, "institution": {"code": "987", "name": "Banco Sintético"},
     "creditData": {"holder": "PORTADOR SINTETICO"}},
]

TRANSACOES = {
    "k1": [
        tx("k-1", "2026-11-02T13:00:00.000Z", -250.10, "DEBIT", descricao="Mercado Sintético"),
        tx("k-2", "2026-11-03T13:00:00.000Z", 0, "DEBIT", descricao="Ajuste sintético"),
        tx("k-3", "2026-11-04T13:00:00.000Z", 3000.00, "CREDIT", descricao="",
           categoria=("02000000", "Income")),
        tx("k-4", "2026-11-05T13:00:00.000Z", -500.00, "DEBIT", descricao="Pagamento fatura sintética",
           categoria=("05100000", "Credit card payment")),
    ],
    "c1": [
        tx("c-1", "2026-09-20T13:00:00.000Z", 500.00, "DEBIT", bill="b1"),
        tx("c-2", "2026-10-10T13:00:00.000Z", -500.00, "CREDIT", bill="b1",
           categoria=("05100000", "Credit card payment")),
        tx("c-3", "2026-11-01T13:00:00.000Z", 1000.00, "DEBIT"),
        tx("c-4", "2026-11-10T13:00:00.000Z", 200.00, "DEBIT", status="PENDING"),
        tx("c-5", "2026-12-05T13:00:00.000Z", 300.00, "DEBIT", parcela=(2, 3)),
    ],
}

FATURAS = {"c1": [{"id": "b1", "dueDate": "2026-10-10T00:00:00.000Z", "totalAmount": 500.00}]}


class Pluggy(BaseHTTPRequestHandler):
    registro = None

    def log_message(self, *args):  # silencioso
        pass

    def _registrar(self):
        with open(Pluggy.registro, "a", encoding="utf-8") as f:
            f.write(f"{self.command} {self.path}\n")

    def _json(self, codigo, corpo):
        dados = json.dumps(corpo).encode()
        self.send_response(codigo)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(dados)))
        self.end_headers()
        self.wfile.write(dados)

    def do_POST(self):
        self._registrar()
        if self.path != "/auth":
            return self._json(404, {"message": "não existe"})
        tamanho = int(self.headers.get("Content-Length", 0))
        pedido = json.loads(self.rfile.read(tamanho) or b"{}")
        if pedido.get("clientId") != CLIENT_ID or pedido.get("clientSecret") != CLIENT_SECRET:
            return self._json(401, {"message": "credenciais inválidas", "code": 401})
        return self._json(200, {"apiKey": API_KEY})

    def do_GET(self):
        self._registrar()
        if self.headers.get("X-API-KEY") != API_KEY:
            return self._json(403, {"message": "sem chave"})
        url = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(url.query).items()}
        if url.path == f"/items/{ITEM}":
            return self._json(200, {"id": ITEM, "status": "UPDATED", "executionStatus": "SUCCESS",
                                    "connector": {"id": 600, "name": "MeuPluggy"},
                                    "consentExpiresAt": "2027-11-11T00:00:00.000Z"})
        if url.path == "/accounts" and q.get("itemId") == ITEM:
            return self._json(200, {"results": CONTAS, "total": len(CONTAS)})
        if url.path == "/v2/transactions":
            todas = TRANSACOES.get(q.get("accountId"), [])
            todas = [t for t in todas if t["date"][:10] >= q.get("dateFrom", "0000")]
            if q.get("after") == "pagina-2":
                return self._json(200, {"results": todas[2:], "next": None})
            proximo = f"/v2/transactions?accountId={q.get('accountId')}&after=pagina-2" if len(todas) > 2 else None
            return self._json(200, {"results": todas[:2], "next": proximo})
        if url.path == "/bills":
            return self._json(200, {"results": FATURAS.get(q.get("accountId"), [])})
        if url.path == "/transactions":
            return self._json(410, {"message": "gone"})
        return self._json(404, {"message": "não existe"})


def main():
    arquivo_porta, Pluggy.registro = sys.argv[1], sys.argv[2]
    servidor = ThreadingHTTPServer(("127.0.0.1", 0), Pluggy)
    with open(arquivo_porta, "w", encoding="utf-8") as f:
        f.write(str(servidor.server_address[1]))
    servidor.serve_forever()


if __name__ == "__main__":
    main()
