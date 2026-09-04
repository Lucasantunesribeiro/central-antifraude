using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralAntifraude.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class IntegracoesEIngestao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integracoes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ativa = table.Column<bool>(type: "boolean", nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integracoes", x => x.id);
                    table.ForeignKey(
                        name: "fk_integracoes_organizacoes_organizacao_id",
                        column: x => x.organizacao_id,
                        principalTable: "organizacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "credenciais_de_integracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    integracao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identificador_publico = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hash_do_segredo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    usada_pela_ultima_vez_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revogada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    motivo_da_revogacao = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credenciais_de_integracao", x => x.id);
                    table.ForeignKey(
                        name: "fk_credenciais_de_integracao_integracoes_integracao_id",
                        column: x => x.integracao_id,
                        principalTable: "integracoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transacoes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    integracao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identificador_externo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ocorrida_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recebida_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cliente_externo_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    referencia_do_instrumento = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    fingerprint_do_dispositivo = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    fingerprint_do_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    pais_de_origem = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    chave_de_idempotencia = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    fingerprint_do_payload = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    moeda = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    valor = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transacoes", x => x.id);
                    table.ForeignKey(
                        name: "fk_transacoes_integracoes_integracao_id",
                        column: x => x.integracao_id,
                        principalTable: "integracoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_credenciais_de_integracao_identificador_publico",
                table: "credenciais_de_integracao",
                column: "identificador_publico",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_credenciais_de_integracao_integracao_id",
                table: "credenciais_de_integracao",
                column: "integracao_id");

            migrationBuilder.CreateIndex(
                name: "ix_integracoes_organizacao_id_nome",
                table: "integracoes",
                columns: new[] { "organizacao_id", "nome" });

            migrationBuilder.CreateIndex(
                name: "ix_transacoes_idempotencia",
                table: "transacoes",
                columns: new[] { "organizacao_id", "integracao_id", "chave_de_idempotencia" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transacoes_identificador_externo",
                table: "transacoes",
                columns: new[] { "organizacao_id", "integracao_id", "identificador_externo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transacoes_integracao_id",
                table: "transacoes",
                column: "integracao_id");

            migrationBuilder.CreateIndex(
                name: "ix_transacoes_organizacao_id_cliente_externo_id_ocorrida_em",
                table: "transacoes",
                columns: new[] { "organizacao_id", "cliente_externo_id", "ocorrida_em" });

            migrationBuilder.CreateIndex(
                name: "ix_transacoes_organizacao_id_recebida_em",
                table: "transacoes",
                columns: new[] { "organizacao_id", "recebida_em" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credenciais_de_integracao");

            migrationBuilder.DropTable(
                name: "transacoes");

            migrationBuilder.DropTable(
                name: "integracoes");
        }
    }
}
