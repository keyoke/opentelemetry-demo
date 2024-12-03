#!/usr/bin/python

# Copyright The OpenTelemetry Authors
# SPDX-License-Identifier: Apache-2.0


import json
import os
import random
import uuid
import logging
import sys, traceback

from locust import HttpUser, task, between
from locust_plugins.users.playwright import PlaywrightUser, pw, PageWithRetry
from locust.exception import RescheduleTask

from opentelemetry import context, baggage, trace
from opentelemetry.metrics import set_meter_provider
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor
from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.instrumentation.jinja2 import Jinja2Instrumentor
from opentelemetry.instrumentation.requests import RequestsInstrumentor
from opentelemetry.instrumentation.system_metrics import SystemMetricsInstrumentor
from opentelemetry.instrumentation.urllib3 import URLLib3Instrumentor
from opentelemetry._logs import set_logger_provider
from opentelemetry.exporter.otlp.proto.grpc._log_exporter import (
    OTLPLogExporter,
)
from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor
from opentelemetry.sdk.resources import Resource

from openfeature import api
from openfeature.contrib.provider.flagd import FlagdProvider
from openfeature.contrib.hook.opentelemetry import TracingHook

from playwright.async_api import Route, Request

logger_provider = LoggerProvider(resource=Resource.create(
        {
            "service.name": "loadgenerator",
        }
    ),)
set_logger_provider(logger_provider)

exporter = OTLPLogExporter(insecure=True)
logger_provider.add_log_record_processor(BatchLogRecordProcessor(exporter))
handler = LoggingHandler(level=logging.INFO, logger_provider=logger_provider)

# Attach OTLP handler to locust logger
logging.getLogger().addHandler(handler)
logging.getLogger().setLevel(logging.INFO)

exporter = OTLPMetricExporter(insecure=True)
set_meter_provider(MeterProvider([PeriodicExportingMetricReader(exporter)]))

tracer_provider = TracerProvider()
trace.set_tracer_provider(tracer_provider)
tracer_provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))

# Instrumenting manually to avoid error with locust gevent monkey
Jinja2Instrumentor().instrument()
RequestsInstrumentor().instrument()
SystemMetricsInstrumentor().instrument()
URLLib3Instrumentor().instrument()
logging.info("Instrumentation complete")

# Initialize Flagd provider
api.set_provider(FlagdProvider(host=os.environ.get('FLAGD_HOST', 'flagd'), port=os.environ.get('FLAGD_PORT', 8013)))
api.add_hooks([TracingHook()])

def get_flagd_value(FlagName):
    # Initialize OpenFeature
    client = api.get_client()
    return client.get_integer_value(FlagName, 0)

categories = [
    "binoculars",
    "telescopes",
    "accessories",
    "assembly",
    "travel",
    "books",
    None,
]

products = [
    "0PUK6V6EV0",
    "1YMWWN1N4O",
    "2ZYFJ3GM2N",
    "66VCHSJNUP",
    "6E92ZMYYFZ",
    "9SIQT8TOJO",
    "L9ECAV7KIM",
    "LS4PSXUNUM",
    "OLJCESPC7Z",
    "HQTGWGPNH4",
]

people_file = open('people.json')
people = json.load(people_file)

class WebsiteUser(HttpUser):
    wait_time = between(1, 10)

    @task(1)
    def index(self):
        self.client.get("/", headers=self.headers)

    @task(10)
    def browse_product(self):
        self.client.get("/api/products/" + random.choice(products), headers=self.headers)

    @task(3)
    def get_recommendations(self):
        params = {
            "productIds": [random.choice(products)],
            "sessionId": self.session_id,
        }
        self.client.get("/api/recommendations", params=params, headers=self.headers)

    @task(3)
    def get_ads(self):
        params = {
            "contextKeys": [random.choice(categories)],
        }
        self.client.get("/api/data/", params=params, headers=self.headers)

    @task(3)
    def view_cart(self):
        self.client.get("/api/cart", headers=self.headers)

    @task(2)
    def add_to_cart(self, user=""):
        product = random.choice(products)
        self.client.get("/api/products/" + product, headers=self.headers) 
        cart_item = {
            "item": {
                "productId": product,
                "quantity": random.choice([1, 2, 3, 4, 5, 10]),
            },
            "userId": user,
        }
        params = {
            "sessionId":  self.session_id,
        }
        self.client.post("/api/cart", params=params, json=cart_item, headers=self.headers)

    @task(1)
    def checkout(self):
        # checkout call with an item added to cart
        self.add_to_cart(user=self.user_id)
        checkout_person = random.choice(people)
        checkout_person["userId"] = self.user_id
        self.client.post("/api/checkout", json=checkout_person, headers=self.headers)

    @task(1)
    def checkout_multi(self):
        # checkout call which adds 2-4 different items to cart before checkout
        for i in range(random.choice([2, 3, 4])):
            self.add_to_cart(user=self.user_id)
        checkout_person = random.choice(people)
        checkout_person["userId"] = self.user_id
        self.client.post("/api/checkout", json=checkout_person, headers=self.headers)

    @task(5)
    def flood_home(self):
        for _ in range(0, get_flagd_value("loadgeneratorFloodHomepage")):
            self.client.get("/", headers=self.headers)

    def on_start(self):
        # use the same http session for all requests
        self.user_id = str(uuid.uuid4())
        self.session_id = str(uuid.uuid4())
        self.headers = {
            'User-Agent': 'Locust/2.x',
            "Cookie":f"SESSIONID:{self.session_id};USERID:{self.user_id};"
        }
        # normalize cookie seperator on ';' for the http traffic
        # self.client.cookies.set("SESSIONID", self.session_id)
        # self.client.cookies.set("USERID", self.user_id)
        ctx = baggage.set_baggage("session.id", self.session_id)
        ctx = baggage.set_baggage("user.id", self.user_id)
        ctx = baggage.set_baggage("synthetic_request", "true", context=ctx)
        context.attach(ctx)
        self.index()


browser_traffic_enabled = os.environ.get("LOCUST_BROWSER_TRAFFIC_ENABLED", "").lower() in ("true", "yes", "on")

if browser_traffic_enabled:
    class WebsiteBrowserUser(PlaywrightUser):
        weight = 2
        headless = True  # to use a headless browser, without a GUI

        @task(2)
        @pw
        async def open_cart_page_and_change_currency(self, page: PageWithRetry):
            try:
                page.on("console", lambda msg: print(msg.text))
                await page.route('**/*', add_baggage_header)
                await page.goto("/cart", wait_until="domcontentloaded")
                
                # select a random user from the people.json file and checkout
                checkout_details = random.choice(people)
                await page.select_option('[name="currency_code"]', value=str(checkout_details['userCurrency']))

                await page.wait_for_timeout(2000)  # giving the browser time to export the traces
            except Exception as e:
                traceback.print_exc(file=sys.stdout)
                raise RescheduleTask(e)

        @task(2)
        @pw
        async def add_product_to_cart(self, page: PageWithRetry):
            try:
                page.on("console", lambda msg: print(msg.text))
                await page.route('**/*', add_baggage_header)
                await page.goto("/", wait_until="domcontentloaded")

                # Get a random product link and click on it
                product_id = random.choice(products)
                await page.click(f"a[href='/product/{product_id}']")
                
                # Add a random number of products to the cart
                product_count = random.choice([1, 2, 3, 4, 5, 10])
                await page.select_option('select[data-cy="product-quantity"]', value=str(product_count))

                await page.click('button:has-text("Add To Cart")')
                await page.wait_for_timeout(2000)  # giving the browser time to export the traces
            except Exception as e:
                traceback.print_exc(file=sys.stdout)
                raise RescheduleTask(e)
        
        @task(4)
        @pw
        async def add_product_to_cart_and_checkout(self, page: PageWithRetry):
            try:
                page.on("console", lambda msg: print(msg.text))
                await page.route('**/*', add_baggage_header)
                await page.goto("/", wait_until="domcontentloaded")
                
                # Get a random product link and click on it
                product_id = random.choice(products)
                await page.click(f"a[href='/product/{product_id}']")
                
                # Add a random number of products to the cart
                product_count = random.choice([1, 2, 3, 4, 5, 10])
                await page.select_option('select[data-cy="product-quantity"]', value=str(product_count))

                # add the product to our cart
                await page.click('button:has-text("Add To Cart")')

                # select a random user from the people.json file and checkout
                checkout_details = random.choice(people)
                await page.select_option('select[name="currency_code"]', value=str(checkout_details['userCurrency']))

                await page.locator('input#email').fill(checkout_details['email'])
                await page.locator('input#street_address').fill(checkout_details['address']['streetAddress'])
                await page.locator('input#zip_code').fill(str(checkout_details['address']['zipCode']))
                await page.locator('input#city').fill(checkout_details['address']['city'])
                await page.locator('input#state').fill(checkout_details['address']['state'])
                await page.locator('input#country').fill(checkout_details['address']['country'])
                await page.locator('input#credit_card_number').fill(str(checkout_details['creditCard']['creditCardNumber']))
                await page.select_option('select#credit_card_expiration_month', value=str(checkout_details['creditCard']['creditCardExpirationMonth']))
                await page.select_option('select#credit_card_expiration_year', value=str(checkout_details['creditCard']['creditCardExpirationYear']))
                await page.locator('input#credit_card_cvv').fill(str(checkout_details['creditCard']['creditCardCvv']))

                # Complete the order
                await page.click('button:has-text("Place Order")')
                await page.wait_for_timeout(2000)  # giving the browser time to export the traces 
            except Exception as e:
                traceback.print_exc(file=sys.stdout)
                raise RescheduleTask(e)


async def add_baggage_header(route: Route, request: Request):
    existing_baggage = request.headers.get('baggage', '')
    headers = {
        **request.headers,
        'baggage': ', '.join(filter(None, (existing_baggage, 'synthetic_request=true')))
    }
    await route.continue_(headers=headers)
