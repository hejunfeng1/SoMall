﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TT.Abp.AppManagement.Apps;
using TT.Abp.Mall.Application.Orders.Dtos;
using TT.Abp.Mall.Application.Shops;
using TT.Abp.Mall.Domain.Orders;
using TT.Abp.Mall.Domain.Pays;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using TT.Abp.Mall.Domain.Products;
using TT.Abp.Mall.Domain.Shops;
using TT.Extensions;
using TT.HttpClient.Weixin;
using Volo.Abp;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Guids;
using Volo.Abp.Settings;

namespace TT.Abp.Mall.Application.Orders
{
    public interface IProductOrderAppService : ICrudAppService<ProductOrderDto, Guid, MallRequestDto, ProductOrderCreateOrUpdateDto, ProductOrderCreateOrUpdateDto>
    {
        Task<PagedResultDto<ProductOrderDto>> GetPublicListAsync(MallRequestDto input);
    }

    public class ProductOrderAppService :
        CrudAppService<ProductOrder, ProductOrderDto, Guid, MallRequestDto, ProductOrderCreateOrUpdateDto, ProductOrderCreateOrUpdateDto>,
        IProductOrderAppService
    {
        private readonly IPayApi _payApi;
        private readonly IPayOrderRepository _payOrderRepository;
        private readonly IMallShopLookupService _mallShopLookupService;
        private readonly ISettingProvider _setting;
        private readonly IHttpContextAccessor _httpContext;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IAppProvider _appProvider;


        public ProductOrderAppService(
            IPayApi payApi,
            IRepository<ProductOrder, Guid> repository,
            IPayOrderRepository payOrderRepository,
            IMallShopLookupService mallShopLookupService,
            ISettingProvider setting,
            IHttpContextAccessor httpContext,
            IHttpContextAccessor httpContextAccessor,
            IAppProvider appProvider
        ) : base(repository)
        {
            _payApi = payApi;
            _payOrderRepository = payOrderRepository;
            _mallShopLookupService = mallShopLookupService;
            _setting = setting;
            _httpContext = httpContext;
            _httpContextAccessor = httpContextAccessor;
            _appProvider = appProvider;

            base.GetListPolicyName = MallPermissions.ProductOrders.Default;
            base.GetPolicyName = MallPermissions.ProductOrders.Default;
            base.UpdatePolicyName = MallPermissions.ProductOrders.Update;
            base.DeletePolicyName = MallPermissions.ProductOrders.Delete;
        }

        public override async Task<ProductOrderDto> GetAsync(Guid id)
        {
            var entity = await Repository.Include(x => x.OrderItems).FirstOrDefaultAsync(x => x.Id == id);

            if (entity.CreatorId != CurrentUser.Id)
            {
                await CheckGetPolicyAsync();
            }

            return MapToGetOutputDto(entity);
        }

        public override async Task<PagedResultDto<ProductOrderDto>> GetListAsync(MallRequestDto input)
        {
            var result = await base.GetListAsync(input);

            var shopDictionary = new Dictionary<Guid, MallShopDto>();

            foreach (var dto in result.Items)
            {
                if (dto.ShopId.HasValue)
                {
                    if (!shopDictionary.ContainsKey(dto.ShopId.Value))
                    {
                        var shop = await _mallShopLookupService.FindByIdAsync(dto.ShopId.Value);
                        if (shop != null)
                        {
                            shopDictionary[shop.Id] = ObjectMapper.Map<MallShop, MallShopDto>(shop);
                        }
                    }

                    if (shopDictionary.ContainsKey(dto.ShopId.Value))
                    {
                        dto.Shop = shopDictionary[(Guid) dto.ShopId];
                    }
                }
            }

            return result;
        }


        public override Task<ProductOrderDto> CreateAsync(ProductOrderCreateOrUpdateDto input)
        {
            throw new Exception("not use");
        }

        protected override IQueryable<ProductOrder> CreateFilteredQuery(MallRequestDto input)
        {
            return base.CreateFilteredQuery(input)
                .WhereIf(input.ShopId.HasValue, x => x.ShopId == input.ShopId)
                .Include(x => x.OrderItems);
        }


        #region ForClient

        [HttpPost]
        public async Task<object> PayAsync(OrderPayRequestDto input)
        {
            var productOrder = await Repository.Include(x => x.OrderItems).FirstOrDefaultAsync(x => x.Id == input.OrderId);
            
            var appName = _httpContextAccessor?.HttpContext.Request.Headers["AppName"].FirstOrDefault();
            var app = await _appProvider.GetOrNullAsync(appName);
            var appid = app["appid"] ?? throw new AbpException($"App:{appName} appid未设置");

            var mchId = await _setting.GetOrNullAsync(MallManagementSetting.PayMchId);
            var mchKey = await _setting.GetOrNullAsync(MallManagementSetting.PayKey);
            var notifyUrl = await _setting.GetOrNullAsync(MallManagementSetting.PayNotify);


            var payorder = new PayOrder();
            payorder.CreatWxPayFromProductOrder(id: GuidGenerator.Create(),
                productOrder: productOrder,
                mchId: mchId,
                openid: input.openid,
                appName: appName,
                shareFromUserId: null,
                partnerId: null
            );

            var insertAsync = await _payOrderRepository.InsertAsync(payorder, autoSave: true);

            productOrder.SetBillNo(insertAsync.Id, insertAsync.BillNo);

            var result = await _payApi.UnifiedOrderAsync(
                appid,
                mchId,
                mchKey,
                body: productOrder.OrderItems.First().SpuName,
                outTradeNo: insertAsync.BillNo,
                totalFee: Convert.ToInt32(productOrder.PriceOriginal * 100),
                notifyUrl: notifyUrl,
                tradeType: Consts.TradeType.JsApi,
                openId: input.openid,
                billCreateIp: _httpContext.HttpContext.Connection.RemoteIpAddress.ToString()
            );


            return result;
        }


        [Authorize]
        public async Task<PagedResultDto<ProductOrderDto>> GetPublicListAsync(MallRequestDto input)
        {
            var query = CreateFilteredQuery(input).Where(x => x.CreatorId == CurrentUser.Id);

            var totalCount = await AsyncQueryableExecuter.CountAsync(query);

            query = ApplySorting(query, input);

            query = ApplyPaging(query, input);

            var entities = await AsyncQueryableExecuter.ToListAsync(query);

            var result = new PagedResultDto<ProductOrderDto>(
                totalCount,
                entities.Select(MapToGetListOutputDto).ToList()
            );

            var shopDictionary = new Dictionary<Guid, MallShopDto>();

            foreach (var dto in result.Items)
            {
                if (dto.ShopId.HasValue)
                {
                    if (!shopDictionary.ContainsKey(dto.ShopId.Value))
                    {
                        var shop = await _mallShopLookupService.FindByIdAsync(dto.ShopId.Value);
                        if (shop != null)
                        {
                            shopDictionary[shop.Id] = ObjectMapper.Map<MallShop, MallShopDto>(shop);
                        }
                    }

                    if (shopDictionary.ContainsKey(dto.ShopId.Value))
                    {
                        dto.Shop = shopDictionary[(Guid) dto.ShopId];
                    }
                }
            }

            return result;
        }

        #endregion
    }
}