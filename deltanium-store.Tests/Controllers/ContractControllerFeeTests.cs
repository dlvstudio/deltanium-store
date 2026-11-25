using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeltaniumStore.Controllers;
using DeltaniumStore.Models;
using DeltaniumStore.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace deltanium_store.Tests.Controllers;

public class ContractControllerFeeTests
{
    private const string App = "03699b56070f41068ca1166169547a4bd05022c41e184069352163dfe21553a14c";

    private static async Task<StorageContract?> Create(decimal? centralCharge, long size = 2_108)
    {
        StorageContract? saved = null;
        var svc = new Mock<IStorageContractService>();
        svc.Setup(s => s.CreateContractAsync(It.IsAny<StorageContract>()))
            .ReturnsAsync((StorageContract c) => saved = c);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StorageNode:Pricing:BasePricePerGBPerMonth"] = "800",
            ["StorageNode:Pricing:MinContractFee"] = "1",
        }).Build();
        var controller = new ContractController(svc.Object, config, NullLogger<ContractController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider()
                }
            }
        };
        controller.HttpContext.Request.Headers["X-User-PubKey"] = App;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = await controller.CreateContractAsync(new CreateContractRequest
        {
            ContractType = "TimeFixed",
            StartDateUnix = now,
            EndDateUnix = now + 365 * 86400,
            TotalFileSize = size,
            CentralCharge = centralCharge,
        });
        if (result is BadRequestObjectResult) return null;
        result.Should().BeOfType<OkObjectResult>();
        return saved;
    }

    [Fact]
    public async Task Contract_records_the_central_charge_so_the_fee_shown_is_the_fee_paid()
    {
        (await Create(1.25m))!.TotalFee.Should().Be(1.25m);
        (await Create(0m))!.TotalFee.Should().Be(0m); // social items are free
    }

    [Fact]
    public async Task Without_a_central_charge_the_store_prices_it_with_its_own_minimum()
    {
        (await Create(null))!.TotalFee.Should().Be(1m);
    }

    [Fact]
    public async Task Negative_charge_is_refused()
    {
        (await Create(-1m)).Should().BeNull();
    }
}
