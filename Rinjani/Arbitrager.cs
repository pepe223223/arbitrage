﻿using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using static System.Threading.Thread;
using Rinjani.Properties;

namespace Rinjani
{
    public class Arbitrager : IArbitrager
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private readonly List<Order> _activeOrders = new List<Order>();
        private readonly IBrokerAdapterRouter _brokerAdapterRouter;
        private readonly IConfigStore _configStore;
        private readonly IPositionService _positionService;
        private readonly IQuoteAggregator _quoteAggregator;
        private readonly ISpreadAnalyzer _spreadAnalyzer;

        public Arbitrager(IQuoteAggregator quoteAggregator,
            IConfigStore configStore,
            IPositionService positionService,
            IBrokerAdapterRouter brokerAdapterRouter,
            ISpreadAnalyzer spreadAnalyzer)
        {
            _quoteAggregator = quoteAggregator ?? throw new ArgumentNullException(nameof(quoteAggregator));
            _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
            _brokerAdapterRouter = brokerAdapterRouter ?? throw new ArgumentNullException(nameof(brokerAdapterRouter));
            _spreadAnalyzer = spreadAnalyzer ?? throw new ArgumentNullException(nameof(spreadAnalyzer));
            _positionService = positionService ?? throw new ArgumentNullException(nameof(positionService));
        }

        public void Start()
        {
            Log.Info(Resources.StartingArbitrager, nameof(Arbitrager));
            _quoteAggregator.QuoteUpdated += QuoteUpdated;
            Log.Info(Resources.StartedArbitrager, nameof(Arbitrager));
        }

        public void Dispose()
        {
            _positionService?.Dispose();
            _quoteAggregator?.Dispose();
        }

        private void Arbitrage()
        {
            Log.Info(Resources.LookingForOpportunity);
            var config = _configStore.Config;
            CheckMaxNetExposure();
            SpreadAnalysisResult result;
            try
            {
                result = _spreadAnalyzer.Analyze(_quoteAggregator.Quotes);
            }
            catch (Exception ex)
            {
                Log.Warn(Resources.FailedToGetASpreadAnalysisResult, ex.Message);
                return;
            }

            var bestBid = result.BestBid;
            var bestAsk = result.BestAsk;
            var invertedSpread = result.InvertedSpread;
            var availableVolume = result.AvailableVolume;
            var targetVolume = result.TargetVolume;
            var targetProfit = result.TargetProfit;

            Log.Info("{0,-17}: {1}", Resources.BestAsk, bestAsk);
            Log.Info("{0,-17}: {1}", Resources.BestBid, bestBid);
            Log.Info("{0,-17}: {1}", Resources.Spread, -invertedSpread);
            Log.Info("{0,-17}: {1}", Resources.AvailableVolume, availableVolume);
            Log.Info("{0,-17}: {1}", Resources.TargetVolume, targetVolume);
            Log.Info("{0,-17}: {1}", Resources.ExpectedProfit, targetProfit);

            if (invertedSpread <= 0)
            {
                Log.Info(Resources.NoArbitrageOpportunitySpreadIsNotInverted);
                return;
            }

            Log.Info(Resources.FoundInvertedQuotes);
            if (availableVolume < config.MinSize)
            {
                Log.Info(Resources.AvailableVolumeIsSmallerThanMinSize);
                return;
            }

            if (targetProfit < config.MinTargetProfit)
            {
                Log.Info(Resources.TargetProfitIsSmallerThanMinProfit);
                return;
            }

            if (bestBid.Broker == bestAsk.Broker)
            {
                Log.Warn($"Ignoring intra-broker cross.");
                return;
            }

            if (config.DemoMode)
            {
                Log.Info(Resources.ThisIsDemoModeNotSendingOrders);
                return;
            }

            Log.Info(Resources.FoundArbitrageOppotunity);
            Log.Info(Resources.SendingOrderTargettingQuote, bestAsk);
            SendOrder(bestAsk, targetVolume, OrderType.Limit);
            Log.Info(Resources.SendingOrderTargettingQuote, bestBid);
            SendOrder(bestBid, targetVolume, OrderType.Limit);
            CheckOrderState();
            Log.Info(Resources.SleepingAfterSend, config.SleepAfterSend);
            _activeOrders.Clear();
            Sleep(config.SleepAfterSend);
        }

        private void CheckMaxNetExposure()
        {
            if (Math.Abs(_positionService.NetExposure) > _configStore.Config.MaxNetExposure)
            {
                var message = Resources.NetExposureIsLargerThanMaxNetExposure;
                throw new InvalidOperationException(message);
            }
        }

        private void CheckOrderState()
        {
            var buyOrder = _activeOrders.First(x => x.Side == OrderSide.Buy);
            var sellOrder = _activeOrders.First(x => x.Side == OrderSide.Sell);
            var config = _configStore.Config;
            foreach (var i in Enumerable.Range(1, config.MaxRetryCount))
            {
                Sleep(config.OrderStatusCheckInterval);
                Log.Info(Resources.OrderCheckAttempt, i);
                Log.Info(Resources.CheckingIfBothLegsAreDoneOrNot);

                try
                {
                    _brokerAdapterRouter.Refresh(buyOrder);
                    _brokerAdapterRouter.Refresh(sellOrder);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex.Message);
                    Log.Debug(ex);
                }

                if (buyOrder.Status != OrderStatus.Filled)
                {
                    Log.Warn(Resources.BuyLegIsNotFilledYetPendingSizeIs, sellOrder.PendingSize);
                }
                if (sellOrder.Status != OrderStatus.Filled)
                {
                    Log.Warn(Resources.SellLegIsNotFilledYetPendingSizeIs, sellOrder.PendingSize);
                }

                if (buyOrder.Status == OrderStatus.Filled && sellOrder.Status == OrderStatus.Filled)
                {
                    var profit = Math.Round(sellOrder.FilledSize * sellOrder.AverageFilledPrice -
                                 buyOrder.FilledSize * buyOrder.AverageFilledPrice);
                    Log.Info(Resources.BothLegsAreSuccessfullyFilled);
                    Log.Info(Resources.BuyFillPriceIs, buyOrder.AverageFilledPrice);
                    Log.Info(Resources.SellFillPriceIs, sellOrder.AverageFilledPrice);
                    Log.Info(Resources.ProfitIs, profit);
                    break;
                }

                if (i == config.MaxRetryCount)
                {
                    Log.Warn(Resources.MaxRetryCountReachedCancellingThePendingOrders);
                    if (buyOrder.Status != OrderStatus.Filled)
                    {
                        _brokerAdapterRouter.Cancel(buyOrder);
                    }

                    if (sellOrder.Status != OrderStatus.Filled)
                    {
                        _brokerAdapterRouter.Cancel(sellOrder);
                    }
                    break;
                }
            }
        }

        private void QuoteUpdated(object sender, EventArgs e)
        {
            try
            {
                Log.Info(Util.Hr(20) + "ARBITRAGER" + Util.Hr(20));
                Arbitrage();
                Log.Info(Util.Hr(50));
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                Log.Debug(ex);
                if (Environment.UserInteractive)
                {
                    Log.Error(Resources.ArbitragerThreadHasBeenStopped);
                    _positionService.Dispose();
                    Console.ReadLine();
                }
                Environment.Exit(-1);
            }
        }

        private void SendOrder(Quote quote, decimal targetVolume, OrderType orderType)
        {
            var brokerConfig = _configStore.Config.Brokers.First(x => x.Broker == quote.Broker);
            var orderSide = quote.Side == QuoteSide.Ask ? OrderSide.Buy : OrderSide.Sell;
            var cashMarginType = brokerConfig.CashMarginType;
            var leverageLevel = brokerConfig.LeverageLevel;
            var order = new Order(quote.Broker, orderSide, targetVolume, quote.Price, cashMarginType, orderType,
                leverageLevel);
            _brokerAdapterRouter.Send(order);
            _activeOrders.Add(order);
        }
    }
}