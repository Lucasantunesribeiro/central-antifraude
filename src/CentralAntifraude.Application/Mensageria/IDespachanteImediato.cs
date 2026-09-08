namespace CentralAntifraude.Application.Mensageria;

/// <summary>
/// Acorda o despachante da Outbox logo depois do commit.
///
/// **Por que isto existe, e por que não substitui a Outbox.**
///
/// Na nuvem, o despachante deixou de ser um laço que consulta o banco a cada
/// dois segundos e passou a ser uma função invocada. Sem alguém para acordá-la,
/// o evento esperaria a varredura seguinte — que roda a cada quinze minutos,
/// porque uma varredura mais frequente manteria o banco acordado o mês inteiro
/// e estouraria a camada gratuita (`docs/custo-e-infraestrutura.md`, seção 4).
///
/// Quinze minutos de espera para um alerta é inaceitável. Daí o disparo
/// imediato: terminada a transação, avisa-se o despachante, e o evento sai em
/// segundos.
///
/// **Isto é otimização de latência, e não a garantia.** A chamada acontece
/// DEPOIS do commit e pode falhar — rede, permissão, função indisponível. Se
/// falhasse e nada mais existisse, o evento se perderia em silêncio, que é
/// exatamente o desastre que a Outbox foi criada para impedir (CLAUDE.md seção
/// 38). Por isso:
///
/// - a falha aqui é **engolida**: quem grava não pode ser derrubado por quem
///   avisa;
/// - a varredura periódica continua sendo a rede de segurança, e é ela que
///   sustenta a promessa de entrega.
///
/// Um despachante que não acorda atrasa o alerta em até quinze minutos. Um
/// commit que falha porque o aviso falhou perderia a transação inteira — e a
/// transação é o que o integrador confiou ao produto.
/// </summary>
public interface IDespachanteImediato
{
    /// <summary>
    /// Pede que a Outbox seja despachada agora.
    ///
    /// Não lança: qualquer falha é registrada e engolida. Chamar isto NUNCA
    /// pode alterar o resultado da operação que acabou de ser confirmada.
    /// </summary>
    Task AcordarAsync(CancellationToken cancellationToken);
}
