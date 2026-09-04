using CentralAntifraude.Domain.Auditoria;

namespace CentralAntifraude.Application.Auditoria;

/// <summary>
/// Grava na trilha somente-insercao.
///
/// Nao ha metodo de leitura aqui: a consulta de auditoria e a Fase 10, e uma
/// interface que so escreve deixa obvio que nada nesta fase pode alterar o
/// que ja foi registrado.
/// </summary>
public interface IRegistradorDeAuditoria
{
    Task RegistrarAsync(RegistroDeAuditoria registro, CancellationToken cancellationToken);
}
