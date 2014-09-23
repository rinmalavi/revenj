#!/bin/sh

exec cp \
	../../../Features/Revenj.Features.Storage/bin/Mono/Revenj.Features.Storage.dll* \
	../../../Features/Revenj.Features.Mailer/bin/Debug/Revenj.Features.Mailer.dll* \
	../../../Features/Revenj.Features.RestCache/bin/Release/Revenj.Features.RestCache.dll* \
	../../../Plugins/Revenj.Plugins.DatabasePersistence.Postgres/bin/Mono/Revenj.Plugins.DatabasePersistence.Postgres.dll* \
	../../../Plugins/Revenj.Plugins.Rest.Commands/bin/Mono/Revenj.Plugins.Rest.Commands.dll* \
	../../../Plugins/Revenj.Plugins.Server.Commands/bin/Mono/Revenj.Plugins.Server.Commands.dll* \
	../bin/Debug/

