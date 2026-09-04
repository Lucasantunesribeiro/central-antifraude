using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralAntifraude.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class MotorDeRisco : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "perfis_de_risco",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_perfis_de_risco", x => x.id);
                    table.ForeignKey(
                        name: "fk_perfis_de_risco_organizacoes_organizacao_id",
                        column: x => x.organizacao_id,
                        principalTable: "organizacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "regras",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    nome = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_regras", x => x.id);
                    table.ForeignKey(
                        name: "fk_regras_organizacoes_organizacao_id",
                        column: x => x.organizacao_id,
                        principalTable: "organizacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "versoes_de_perfil_de_risco",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    perfil_id = table.Column<Guid>(type: "uuid", nullable: false),
                    numero = table.Column<int>(type: "integer", nullable: false),
                    limiar_de_revisao = table.Column<int>(type: "integer", nullable: false),
                    limiar_de_bloqueio = table.Column<int>(type: "integer", nullable: false),
                    publicada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_versoes_de_perfil_de_risco", x => x.id);
                    table.ForeignKey(
                        name: "fk_versoes_de_perfil_de_risco_perfis_de_risco_perfil_id",
                        column: x => x.perfil_id,
                        principalTable: "perfis_de_risco",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "versoes_de_regra",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    regra_id = table.Column<Guid>(type: "uuid", nullable: false),
                    numero = table.Column<int>(type: "integer", nullable: false),
                    tipo = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    configuracao = table.Column<string>(type: "jsonb", nullable: false),
                    pontos = table.Column<int>(type: "integer", nullable: false),
                    publicada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_versoes_de_regra", x => x.id);
                    table.ForeignKey(
                        name: "fk_versoes_de_regra_regras_regra_id",
                        column: x => x.regra_id,
                        principalTable: "regras",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "avaliacoes_de_risco",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    versao_de_perfil_id = table.Column<Guid>(type: "uuid", nullable: false),
                    numero_da_versao_de_perfil = table.Column<int>(type: "integer", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    decisao = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    versao_do_motor_usada = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    avaliada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_avaliacoes_de_risco", x => x.id);
                    table.ForeignKey(
                        name: "fk_avaliacoes_de_risco_transacoes_transacao_id",
                        column: x => x.transacao_id,
                        principalTable: "transacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_avaliacoes_de_risco_versoes_de_perfil_de_risco_versao_de_pe",
                        column: x => x.versao_de_perfil_id,
                        principalTable: "versoes_de_perfil_de_risco",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "versoes_de_perfil_regras",
                columns: table => new
                {
                    versao_de_perfil_de_risco_id = table.Column<Guid>(type: "uuid", nullable: false),
                    versoes_de_regra_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_versoes_de_perfil_regras", x => new { x.versao_de_perfil_de_risco_id, x.versoes_de_regra_id });
                    table.ForeignKey(
                        name: "fk_versoes_de_perfil_regras_versoes_de_perfil_de_risco_versao_",
                        column: x => x.versao_de_perfil_de_risco_id,
                        principalTable: "versoes_de_perfil_de_risco",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_versoes_de_perfil_regras_versoes_de_regra_versoes_de_regra_",
                        column: x => x.versoes_de_regra_id,
                        principalTable: "versoes_de_regra",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sinais_de_risco",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    avaliacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    regra_id = table.Column<Guid>(type: "uuid", nullable: false),
                    versao_de_regra_id = table.Column<Guid>(type: "uuid", nullable: false),
                    numero_da_versao_de_regra = table.Column<int>(type: "integer", nullable: false),
                    pontos = table.Column<int>(type: "integer", nullable: false),
                    explicacao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    dados_da_evidencia = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sinais_de_risco", x => x.id);
                    table.ForeignKey(
                        name: "fk_sinais_de_risco_avaliacoes_de_risco_avaliacao_id",
                        column: x => x.avaliacao_id,
                        principalTable: "avaliacoes_de_risco",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sinais_de_risco_versoes_de_regra_versao_de_regra_id",
                        column: x => x.versao_de_regra_id,
                        principalTable: "versoes_de_regra",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_avaliacoes_de_risco_organizacao_id_decisao_avaliada_em",
                table: "avaliacoes_de_risco",
                columns: new[] { "organizacao_id", "decisao", "avaliada_em" });

            migrationBuilder.CreateIndex(
                name: "ix_avaliacoes_de_risco_transacao",
                table: "avaliacoes_de_risco",
                column: "transacao_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_avaliacoes_de_risco_versao_de_perfil_id",
                table: "avaliacoes_de_risco",
                column: "versao_de_perfil_id");

            migrationBuilder.CreateIndex(
                name: "ix_perfis_de_risco_organizacao_id",
                table: "perfis_de_risco",
                column: "organizacao_id");

            migrationBuilder.CreateIndex(
                name: "ix_regras_organizacao_id_tipo",
                table: "regras",
                columns: new[] { "organizacao_id", "tipo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sinais_de_risco_avaliacao_id",
                table: "sinais_de_risco",
                column: "avaliacao_id");

            migrationBuilder.CreateIndex(
                name: "ix_sinais_de_risco_versao_de_regra_id",
                table: "sinais_de_risco",
                column: "versao_de_regra_id");

            migrationBuilder.CreateIndex(
                name: "ix_versoes_de_perfil_de_risco_organizacao_id_publicada_em",
                table: "versoes_de_perfil_de_risco",
                columns: new[] { "organizacao_id", "publicada_em" });

            migrationBuilder.CreateIndex(
                name: "ix_versoes_de_perfil_de_risco_perfil_id_numero",
                table: "versoes_de_perfil_de_risco",
                columns: new[] { "perfil_id", "numero" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_versoes_de_perfil_regras_versoes_de_regra_id",
                table: "versoes_de_perfil_regras",
                column: "versoes_de_regra_id");

            migrationBuilder.CreateIndex(
                name: "ix_versoes_de_regra_regra_id_numero",
                table: "versoes_de_regra",
                columns: new[] { "regra_id", "numero" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sinais_de_risco");

            migrationBuilder.DropTable(
                name: "versoes_de_perfil_regras");

            migrationBuilder.DropTable(
                name: "avaliacoes_de_risco");

            migrationBuilder.DropTable(
                name: "versoes_de_regra");

            migrationBuilder.DropTable(
                name: "versoes_de_perfil_de_risco");

            migrationBuilder.DropTable(
                name: "regras");

            migrationBuilder.DropTable(
                name: "perfis_de_risco");
        }
    }
}
