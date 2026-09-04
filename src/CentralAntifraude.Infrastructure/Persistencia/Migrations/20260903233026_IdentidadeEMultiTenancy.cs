using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralAntifraude.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class IdentidadeEMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organizacoes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    nome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    codigo = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ativa = table.Column<bool>(type: "boolean", nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizacoes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "registros_de_auditoria",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operacao = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    autor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    autor_descricao = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    entidade = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entidade_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    detalhe = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    id_de_correlacao = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ocorrido_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_registros_de_auditoria", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "usuarios",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    nome_completo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    hash_da_senha = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    perfil = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ativo = table.Column<bool>(type: "boolean", nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usuarios", x => x.id);
                    table.ForeignKey(
                        name: "fk_usuarios_organizacoes_organizacao_id",
                        column: x => x.organizacao_id,
                        principalTable: "organizacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    usuario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    familia_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hash_do_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expira_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    usado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revogado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    motivo_da_revogacao = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_usuarios_usuario_id",
                        column: x => x.usuario_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_organizacoes_codigo",
                table: "organizacoes",
                column: "codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_familia_id",
                table: "refresh_tokens",
                column: "familia_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_hash_do_token",
                table: "refresh_tokens",
                column: "hash_do_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_usuario_id",
                table: "refresh_tokens",
                column: "usuario_id");

            migrationBuilder.CreateIndex(
                name: "ix_registros_de_auditoria_organizacao_id_ocorrido_em",
                table: "registros_de_auditoria",
                columns: new[] { "organizacao_id", "ocorrido_em" });

            migrationBuilder.CreateIndex(
                name: "ix_usuarios_email",
                table: "usuarios",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usuarios_organizacao_id_nome_completo",
                table: "usuarios",
                columns: new[] { "organizacao_id", "nome_completo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "registros_de_auditoria");

            migrationBuilder.DropTable(
                name: "usuarios");

            migrationBuilder.DropTable(
                name: "organizacoes");
        }
    }
}
