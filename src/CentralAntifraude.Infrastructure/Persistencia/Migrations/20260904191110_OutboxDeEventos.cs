using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralAntifraude.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class OutboxDeEventos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "eventos_de_saida",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ocorrido_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    id_de_correlacao = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    conteudo = table.Column<string>(type: "jsonb", nullable: false),
                    publicado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tentativas_de_publicacao = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eventos_de_saida", x => x.id);
                    table.ForeignKey(
                        name: "fk_eventos_de_saida_organizacoes_organizacao_id",
                        column: x => x.organizacao_id,
                        principalTable: "organizacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_eventos_de_saida_organizacao_id",
                table: "eventos_de_saida",
                column: "organizacao_id");

            migrationBuilder.CreateIndex(
                name: "ix_eventos_de_saida_pendentes",
                table: "eventos_de_saida",
                columns: new[] { "publicado_em", "ocorrido_em" },
                filter: "publicado_em IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eventos_de_saida");
        }
    }
}
