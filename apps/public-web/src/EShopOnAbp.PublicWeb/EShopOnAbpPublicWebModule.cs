﻿using EShopOnAbp.BasketService;
using EShopOnAbp.CatalogService;
using EShopOnAbp.CmskitService;
using EShopOnAbp.Localization;
using EShopOnAbp.OrderingService;
using EShopOnAbp.PaymentService;
using EShopOnAbp.PaymentService.PaymentMethods;
using EShopOnAbp.PublicWeb.AnonymousUser;
using EShopOnAbp.PublicWeb.Components.Toolbar.Cart;
using EShopOnAbp.PublicWeb.Menus;
using EShopOnAbp.PublicWeb.PaymentMethods;
using EShopOnAbp.Shared.Hosting.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Polly;
using StackExchange.Redis;
using System;
using System.Net.Http.Headers;
using Volo.Abp;
using Volo.Abp.Account;
using Volo.Abp.AspNetCore.Authentication.OpenIdConnect;
using Volo.Abp.AspNetCore.Mvc.Client;
using Volo.Abp.AspNetCore.Mvc.Localization;
using Volo.Abp.AspNetCore.Mvc.UI.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Basic;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Basic.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Shared;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Shared.Toolbars;
using Volo.Abp.AspNetCore.SignalR;
using Volo.Abp.AutoMapper;
using Volo.Abp.Caching;
using Volo.Abp.Caching.StackExchangeRedis;
using Volo.Abp.EventBus.RabbitMq;
using Volo.Abp.Http.Client;
using Volo.Abp.Http.Client.IdentityModel.Web;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Volo.Abp.UI.Navigation;
using Volo.Abp.UI.Navigation.Urls;
using Volo.Abp.VirtualFileSystem;
using Volo.CmsKit;
using Volo.CmsKit.Public.Web;
using Yarp.ReverseProxy.Transforms;

namespace EShopOnAbp.PublicWeb;

[DependsOn(
    typeof(AbpCachingStackExchangeRedisModule),
    typeof(AbpEventBusRabbitMqModule),
    typeof(AbpAspNetCoreMvcClientModule),
    typeof(AbpAspNetCoreAuthenticationOpenIdConnectModule),
    typeof(AbpHttpClientIdentityModelWebModule),
    typeof(AbpAspNetCoreMvcUiBasicThemeModule),
    typeof(AbpAccountHttpApiClientModule),
    typeof(EShopOnAbpSharedHostingAspNetCoreModule),
    typeof(EShopOnAbpSharedLocalizationModule),
    typeof(CatalogServiceHttpApiClientModule),
    typeof(BasketServiceContractsModule),
    typeof(OrderingServiceHttpApiClientModule),
    typeof(AbpAspNetCoreSignalRModule),
    typeof(PaymentServiceHttpApiClientModule),
    typeof(AbpAutoMapperModule),
    typeof(CmskitServiceHttpApiClientModule),
    typeof(CmsKitDomainModule),
    typeof(CmsKitPublicWebModule)


)]
public class EShopOnAbpPublicWebModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.PreConfigure<AbpMvcDataAnnotationsLocalizationOptions>(options =>
        {
            options.AddAssemblyResource(
                typeof(EShopOnAbpResource),
                typeof(EShopOnAbpPublicWebModule).Assembly
            );
        });

        PreConfigure<AbpHttpClientBuilderOptions>(options =>
        {
            options.ProxyClientBuildActions.Add((remoteServiceName, clientBuilder) =>
            {
                clientBuilder.AddTransientHttpErrorPolicy(policyBuilder =>
                    policyBuilder.WaitAndRetryAsync(
                        3,
                        i => TimeSpan.FromSeconds(Math.Pow(2, i))
                    )
                );
            });
        });

        FeatureConfigurer.Configure();
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
        var configuration = context.Services.GetConfiguration();

        ConfigureBasketHttpClient(context);

        context.Services.AddAutoMapperObjectMapper<EShopOnAbpPublicWebModule>();
        Configure<AbpAutoMapperOptions>(options => { options.AddMaps<EShopOnAbpPublicWebModule>(validate: true); });

        Configure<AbpBundlingOptions>(options =>
        {
            options.StyleBundles.Configure(
                BasicThemeBundles.Styles.Global,
                bundle => { bundle.AddContributors(typeof(CartWidgetStyleContributor)); }
            );
        });

        context.Services.Configure<AbpRemoteServiceOptions>(options =>
        {
            options.RemoteServices.Default =
                new RemoteServiceConfiguration(configuration["RemoteServices:Default:BaseUrl"]);
        });

        Configure<AbpMultiTenancyOptions>(options => { options.IsEnabled = true; });

        Configure<AbpDistributedCacheOptions>(options => { options.KeyPrefix = "EShopOnAbp:"; });

        Configure<AppUrlOptions>(options => { options.Applications["MVC"].RootUrl = configuration["App:SelfUrl"]; });

        ConfigurePayment(configuration);

        context.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = "Cookies";
                options.DefaultChallengeScheme = "oidc";
            })
            .AddCookie("Cookies", options => { options.ExpireTimeSpan = TimeSpan.FromDays(365); })
            .AddAbpOpenIdConnect("oidc", options =>
            {
                /*
                 * ASP.NET core uses the http://*:5000 and https://*:5001 ports for default communication with the OIDC middleware
                 * The app requires load balancing services to work with :80 or :443
                 * These needs to be added to the keycloak client, in order for the redirect to work.
                 * If you however intend to use the app by itself then,
                 * Change the ports in launchsettings.json, but beware to also change the options.CallbackPath and options.SignedOutCallbackPath!
                 * Use LB services whenever possible, to reduce the config hazzle :)
                */

                //Use default signin scheme
                // options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                //Keycloak server
                options.Authority = configuration["Keycloak:ServerRealm"];
                //Keycloak client ID
                options.ClientId = configuration["Keycloak:ClientId"];
                //Keycloak client secret
                options.ClientSecret = configuration["Keycloak:ClientSecret"];
                //Keycloak .wellknown config origin to fetch config
                options.MetadataAddress = configuration["Keycloak:Metadata"];
                //Require keycloak to use SSL
                options.RequireHttpsMetadata = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                //Save the token
                options.SaveTokens = true;
                //Token response type, will sometimes need to be changed to IdToken, depending on config.
                options.ResponseType = OpenIdConnectResponseType.Code;
                //SameSite is needed for Chrome/Firefox, as they will give http error 500 back, if not set to unspecified.
                // options.NonceCookie.SameSite = SameSiteMode.Unspecified;
                // options.CorrelationCookie.SameSite = SameSiteMode.Unspecified;
                //
                // options.TokenValidationParameters = new TokenValidationParameters
                // {
                //     NameClaimType = "name",
                //     RoleClaimType = ClaimTypes.Role,
                //     ValidateIssuer = true
                // };
            });
        // .AddAbpOpenIdConnect("oidc", options =>
            // {
            //     options.Authority = configuration["AuthServer:Authority"];
            //     options.RequireHttpsMetadata = Convert.ToBoolean(configuration["AuthServer:RequireHttpsMetadata"]);
            //     options.ResponseType = OpenIdConnectResponseType.CodeIdToken;
            //
            //     options.ClientId = configuration["AuthServer:ClientId"];
            //     options.ClientSecret = configuration["AuthServer:ClientSecret"];
            //
            //     options.SaveTokens = true;
            //     options.GetClaimsFromUserInfoEndpoint = true;
            //
            //     options.Scope.Add("role");
            //     options.Scope.Add("email");
            //     options.Scope.Add("phone");
            //     options.Scope.Add("AccountService");
            //     options.Scope.Add("AdministrationService");
            //     options.Scope.Add("BasketService");
            //     options.Scope.Add("CatalogService");
            //     options.Scope.Add("PaymentService");
            //     options.Scope.Add("OrderingService");
            //     options.Scope.Add("CmskitService");
            // });
        if (Convert.ToBoolean(configuration["AuthServer:IsOnProd"]))
        {
            context.Services.Configure<OpenIdConnectOptions>("oidc", options =>
            {
                options.MetadataAddress = configuration["AuthServer:MetaAddress"].EnsureEndsWith('/') +
                                          ".well-known/openid-configuration";

                var previousOnRedirectToIdentityProvider = options.Events.OnRedirectToIdentityProvider;
                options.Events.OnRedirectToIdentityProvider = async ctx =>
                {
                    // Intercept the redirection so the browser navigates to the right URL in your host
                    ctx.ProtocolMessage.IssuerAddress = configuration["AuthServer:Authority"].EnsureEndsWith('/') + "connect/authorize";

                    if (previousOnRedirectToIdentityProvider != null)
                    {
                        await previousOnRedirectToIdentityProvider(ctx);
                    }
                };
                var previousOnRedirectToIdentityProviderForSignOut = options.Events.OnRedirectToIdentityProviderForSignOut;
                options.Events.OnRedirectToIdentityProviderForSignOut = async ctx =>
                {
                    // Intercept the redirection for signout so the browser navigates to the right URL in your host
                    ctx.ProtocolMessage.IssuerAddress = configuration["AuthServer:Authority"].EnsureEndsWith('/') + "connect/endsession";

                    if (previousOnRedirectToIdentityProviderForSignOut != null)
                    {
                        await previousOnRedirectToIdentityProviderForSignOut(ctx);
                    }
                };
            });
        }

        var redis = ConnectionMultiplexer.Connect(configuration["Redis:Configuration"]);
        context.Services
            .AddDataProtection()
            .PersistKeysToStackExchangeRedis(redis, "EShopOnAbp-Protection-Keys")
            .SetApplicationName("eShopOnAbp-PublicWeb");

        Configure<AbpNavigationOptions>(options =>
        {
            options.MenuContributors.Add(new EShopOnAbpPublicWebMenuContributor(configuration));
        });

        Configure<AbpToolbarOptions>(options =>
        {
            options.Contributors.Add(new EShopOnAbpPublicWebToolbarContributor());
        });

        context.Services
            .AddReverseProxy()
            .LoadFromConfig(configuration.GetSection("ReverseProxy"))
            .AddTransforms(builderContext =>
            {
                builderContext.AddRequestTransform(async (transformContext) =>
                {
                    transformContext.ProxyRequest.Headers
                        .Authorization = new AuthenticationHeaderValue(
                        "Bearer",
                        await transformContext.HttpContext.GetTokenAsync("access_token")
                    );
                });
            });
    }

    private void ConfigureBasketHttpClient(ServiceConfigurationContext context)
    {
        context.Services.AddStaticHttpClientProxies(
            typeof(BasketServiceContractsModule).Assembly, remoteServiceConfigurationName: BasketServiceConstants.RemoteServiceName
        );

        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<EShopOnAbpPublicWebModule>();
        });
    }

    private void ConfigurePayment(IConfiguration configuration)
    {
        Configure<EShopOnAbpPublicWebPaymentOptions>(options =>
        {
            options.PaymentSuccessfulCallbackUrl =
                configuration["App:SelfUrl"].EnsureEndsWith('/') + "PaymentCompleted";
        });

        Configure<PaymentMethodUiOptions>(options =>
        {
            options.ConfigureIcon(PaymentMethodNames.PayPal, "fa-cc-paypal paypal");
        });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.GetApplicationBuilder();
        var env = context.GetEnvironment();

        app.Use((ctx, next) =>
        {
            ctx.Request.Scheme = "https";
            return next();
        });

        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseAbpRequestLocalization();

        if (!env.IsDevelopment())
        {
            app.UseErrorPage();
        }

        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });

        app.UseCorrelationId();
        app.UseStaticFiles();
        app.UseRouting();
        // app.UseHttpMetrics();
        app.UseAuthentication();
        app.UseAbpSerilogEnrichers();
        app.UseAuthorization();
        app.UseAnonymousUser();
        app.UseConfiguredEndpoints(endpoints =>
        {
            endpoints.MapReverseProxy();
            // endpoints.MapMetrics();
        });
    }
}