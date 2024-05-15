﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using UnturnedModdingCollective.Services;

#nullable disable

namespace UnturnedModdingCollective.Migrations
{
    [DbContext(typeof(BotDbContext))]
    [Migration("20240515025740_AddRoleTracking")]
    partial class AddRoleTracking
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.5")
                .HasAnnotation("Relational:MaxIdentifierLength", 64);

            MySqlModelBuilderExtensions.AutoIncrementColumns(modelBuilder);

            modelBuilder.Entity("UnturnedModdingCollective.Models.ApplicableRole", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    MySqlPropertyBuilderExtensions.UseMySqlIdentityColumn(b.Property<int>("Id"));

                    b.Property<string>("Description")
                        .HasMaxLength(100)
                        .HasColumnType("varchar(100)");

                    b.Property<string>("Emoji")
                        .HasMaxLength(32)
                        .HasColumnType("varchar(32)");

                    b.Property<ulong>("GuildId")
                        .HasColumnType("bigint unsigned");

                    b.Property<ulong>("RoleId")
                        .HasColumnType("bigint unsigned");

                    b.Property<ulong>("UserAddedBy")
                        .HasColumnType("bigint unsigned");

                    b.HasKey("Id");

                    b.ToTable("applicable_roles");
                });

            modelBuilder.Entity("UnturnedModdingCollective.Models.ReviewRequest", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    MySqlPropertyBuilderExtensions.UseMySqlIdentityColumn(b.Property<int>("Id"));

                    b.Property<bool?>("Accepted")
                        .HasColumnType("tinyint(1)");

                    b.Property<string>("GlobalName")
                        .HasMaxLength(32)
                        .HasColumnType("varchar(32)");

                    b.Property<ulong>("MessageId")
                        .HasColumnType("bigint unsigned");

                    b.Property<ulong?>("ResubmitApprover")
                        .HasColumnType("bigint unsigned");

                    b.Property<ulong>("Steam64")
                        .HasColumnType("bigint unsigned");

                    b.Property<ulong>("ThreadId")
                        .HasColumnType("bigint unsigned");

                    b.Property<ulong>("UserId")
                        .HasColumnType("bigint unsigned");

                    b.Property<string>("UserName")
                        .HasMaxLength(32)
                        .HasColumnType("varchar(32)");

                    b.Property<DateTime?>("UtcTimeClosed")
                        .HasColumnType("datetime(6)");

                    b.Property<DateTime>("UtcTimeStarted")
                        .HasColumnType("datetime(6)");

                    b.Property<DateTime?>("UtcTimeSubmitted")
                        .HasColumnType("datetime(6)");

                    b.HasKey("Id");

                    b.ToTable("review_requests");
                });

            modelBuilder.Entity("UnturnedModdingCollective.Models.ReviewRequestRoleLink", b =>
                {
                    b.Property<int>("RequestId")
                        .HasColumnType("int")
                        .HasColumnName("Request");

                    b.Property<ulong>("RoleId")
                        .HasColumnType("bigint unsigned");

                    b.HasKey("RequestId", "RoleId");

                    b.ToTable("review_request_roles");
                });

            modelBuilder.Entity("UnturnedModdingCollective.Models.ReviewRequestRoleLink", b =>
                {
                    b.HasOne("UnturnedModdingCollective.Models.ReviewRequest", "Request")
                        .WithMany("RequestedRoles")
                        .HasForeignKey("RequestId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Request");
                });

            modelBuilder.Entity("UnturnedModdingCollective.Models.ReviewRequest", b =>
                {
                    b.Navigation("RequestedRoles");
                });
#pragma warning restore 612, 618
        }
    }
}
