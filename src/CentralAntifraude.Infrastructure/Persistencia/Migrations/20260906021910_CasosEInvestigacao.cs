using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralAntifraude.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class CasosEInvestigacao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "caso_id",
                table: "alertas",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "casos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    titulo = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    responsavel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resultado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    versao = table.Column<int>(type: "integer", nullable: false),
                    aberto_por_id = table.Column<Guid>(type: "uuid", nullable: false),
                    aberto_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolvido_por_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolvido_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    total_de_eventos = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_casos", x => x.id);
                    table.ForeignKey(
                        name: "fk_casos_organizacoes_organizacao_id",
                        column: x => x.organizacao_id,
                        principalTable: "organizacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_casos_usuarios_aberto_por_id",
                        column: x => x.aberto_por_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_casos_usuarios_responsavel_id",
                        column: x => x.responsavel_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "eventos_do_caso",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    caso_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequencia = table.Column<int>(type: "integer", nullable: false),
                    tipo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    autor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    autor_descricao = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    descricao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    referencia_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ocorrido_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eventos_do_caso", x => x.id);
                    table.ForeignKey(
                        name: "fk_eventos_do_caso_casos_caso_id",
                        column: x => x.caso_id,
                        principalTable: "casos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notas_do_caso",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    caso_id = table.Column<Guid>(type: "uuid", nullable: false),
                    autor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    autor_descricao = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    conteudo = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notas_do_caso", x => x.id);
                    table.ForeignKey(
                        name: "fk_notas_do_caso_casos_caso_id",
                        column: x => x.caso_id,
                        principalTable: "casos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "resultados_de_investigacao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    caso_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resultado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    registrado_por_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registrado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resultados_de_investigacao", x => x.id);
                    table.ForeignKey(
                        name: "fk_resultados_de_investigacao_casos_caso_id",
                        column: x => x.caso_id,
                        principalTable: "casos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_resultados_de_investigacao_transacoes_transacao_id",
                        column: x => x.transacao_id,
                        principalTable: "transacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alertas_caso",
                table: "alertas",
                column: "caso_id");

            migrationBuilder.CreateIndex(
                name: "ix_casos_aberto_por_id",
                table: "casos",
                column: "aberto_por_id");

            migrationBuilder.CreateIndex(
                name: "ix_casos_organizacao_responsavel",
                table: "casos",
                columns: new[] { "organizacao_id", "responsavel_id" });

            migrationBuilder.CreateIndex(
                name: "ix_casos_organizacao_status_atualizado",
                table: "casos",
                columns: new[] { "organizacao_id", "status", "atualizado_em" });

            migrationBuilder.CreateIndex(
                name: "ix_casos_responsavel_id",
                table: "casos",
                column: "responsavel_id");

            migrationBuilder.CreateIndex(
                name: "ix_eventos_do_caso_caso_sequencia",
                table: "eventos_do_caso",
                columns: new[] { "caso_id", "sequencia" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notas_do_caso_caso_criada",
                table: "notas_do_caso",
                columns: new[] { "caso_id", "criada_em" });

            migrationBuilder.CreateIndex(
                name: "ix_resultados_de_investigacao_caso_id",
                table: "resultados_de_investigacao",
                column: "caso_id");

            migrationBuilder.CreateIndex(
                name: "ix_resultados_de_investigacao_transacao",
                table: "resultados_de_investigacao",
                column: "transacao_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_alertas_casos_caso_id",
                table: "alertas",
                column: "caso_id",
                principalTable: "casos",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_alertas_casos_caso_id",
                table: "alertas");

            migrationBuilder.DropTable(
                name: "eventos_do_caso");

            migrationBuilder.DropTable(
                name: "notas_do_caso");

            migrationBuilder.DropTable(
                name: "resultados_de_investigacao");

            migrationBuilder.DropTable(
                name: "casos");

            migrationBuilder.DropIndex(
                name: "ix_alertas_caso",
                table: "alertas");

            migrationBuilder.DropColumn(
                name: "caso_id",
                table: "alertas");
        }
    }
}
