using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralAntifraude.Infrastructure.Persistencia.Migrations
{
    /// <summary>
    /// Linha de base do historico de migrations.
    ///
    /// Vazia de proposito: a Fase 0 e fundacao tecnica e nao cria nenhuma
    /// entidade de dominio (ROADMAP.md, criterio "nenhuma feature de dominio
    /// artificial"). Aplicar esta migration cria a tabela
    /// __EFMigrationsHistory e registra o ponto zero do schema, o que e
    /// exatamente o que os testes de integracao verificam contra um
    /// PostgreSQL real.
    ///
    /// As entidades comecam na Fase 1 (organizacoes e usuarios).
    /// </summary>
    public partial class InicialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
