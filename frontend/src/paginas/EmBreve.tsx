export function EmBreve({ titulo, fase }: { titulo: string; fase?: string }) {
  return (
    <>
      <h1 className="titulo">{titulo}</h1>
      <div className="cartao">
        <div className="cartao-nota">{fase ? `Chega na ${fase} do roadmap (docs/SPEC.md).` : "Volte para a visão geral."}</div>
      </div>
    </>
  );
}
