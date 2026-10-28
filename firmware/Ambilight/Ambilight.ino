#include <Arduino.h>
#include <WiFi.h>
#include <NetworkUdp.h>
#include <WebServer.h>
#include <Preferences.h>
#include <mbedtls/md.h>
#include <mbedtls/base64.h>
#include "BoardConfig.h"

Preferences prefs;
WebServer web(80);
NetworkUDP discovery, frames;
rmt_data_t symbols[MAX_LEDS * LED_CHANNELS * 8];
uint8_t colors[MAX_LEDS * 3] = {};
uint8_t packet[16 + MAX_LEDS * 3 + 32];
String deviceId, token, apPassword, pairingCode;
bool powerOn=true, apOpen=false, rmtReady=false, dirty=false, held=false, hadStream=false;
uint16_t ledCount=DEFAULT_LEDS;
int brightness=65, nightBrightness=20;
uint32_t frameSession=0, sequence=0, lastFrame=0, dirtyAt=0, apDeadline=0, lastDraw=0;
uint32_t receivedFrames=0;
uint32_t buttonChanged=0, pressedAt=0, lastReconnect=0;
int buttonRaw=HIGH, buttonStable=HIGH, encoderPrevious=0, encoderSteps=0;
volatile int encoderDelta=0;
int pairAttempts=0;
String serialCommand;

String randomHex() { char b[33]; for(int i=0;i<4;i++)snprintf(b+i*8,9,"%08lx",(unsigned long)esp_random());return String(b); }
bool streaming() { return lastFrame!=0 && (uint32_t)(millis()-lastFrame)<3500; }
void markDirty() { dirty=true;dirtyAt=millis(); }
void saveState() {
 prefs.putBool("power",powerOn);prefs.putUChar("bright",brightness);prefs.putUChar("night",nightBrightness);prefs.putUShort("count",ledCount);dirty=false;
}
String escapeHtml(String s) {s.replace("&","&amp;");s.replace("<","&lt;");s.replace(">","&gt;");s.replace("\"","&quot;");return s;}
bool apRequest() {return apOpen && web.client().localIP()==WiFi.softAPIP();}
bool authorize() {
 if(web.header("Authorization") != "Bearer " + token) {web.send(401,"text/plain; charset=utf-8","Нет доступа. Выполните привязку устройства.");return false;}return true;
}
void openProvisioning() {
 pairingCode=String(100000 + esp_random()%900000);pairAttempts=0;
 apDeadline=millis()+300000;
 apOpen=WiFi.softAP(("Ambilight-"+deviceId.substring(6)).c_str(),apPassword.c_str(),1,false,1);
 if(!apOpen){pairingCode="";Serial.println("ALSETUP failed");return;}
 Serial.printf("\nAmbilight Wi-Fi: Ambilight-%s\nPassword: %s\nSetup: http://192.168.4.1\n",deviceId.substring(6).c_str(),apPassword.c_str());
}
void closeProvisioning() {
 apOpen=false;pairingCode="";
 // Keep AP+STA initialized: switching STA to AP+STA on this ESP32 can stall the radio.
 // Outside the physical setup window the AP is hidden, password protected, and setup routes reject requests.
 WiFi.softAP(("Ambilight-"+deviceId.substring(6)).c_str(),apPassword.c_str(),1,true,1);
}
void configureWeb() {
 const char* headers[]={"Authorization"};web.collectHeaders(headers,1);
 web.on("/",HTTP_GET,[](){
  if(!apRequest()){web.send(403,"text/plain; charset=utf-8","Удерживайте кнопку 5 секунд и подключитесь к сети Ambilight.");return;}
  String page=R"html(<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Ambilight</title><style>body{font:16px Segoe UI,sans-serif;background:#f7f8fa;color:#24262b;max-width:480px;margin:40px auto;padding:24px}input,button{box-sizing:border-box;font:inherit;padding:12px;margin:8px 0 20px;width:100%;border:1px solid #ddd;border-radius:8px}button{background:#3676e8;color:white;border:0}code{font-size:28px;letter-spacing:5px}p{line-height:1.6}</style><h1>Ambilight</h1><p>Подключите ESP32 к домашнему Wi-Fi 2,4 ГГц. Компьютер должен быть в той же локальной сети.</p><form method="post" action="/wifi"><label>Имя сети<input name="ssid" maxlength="32" required autocomplete="off"></label><label>Пароль сети<input name="password" type="password" maxlength="63" autocomplete="new-password"></label><button>Сохранить и подключиться</button></form><p>Код привязки к программе:</p><code>)html";
  page+=pairingCode;page+="</code><p>Сохраните код, вернитесь в домашний Wi-Fi и нажмите «Найти устройства» в Ambilight. Код действует, пока открыта настройка.</p><p>Версия ";page+=FIRMWARE_VERSION;page+="</p></html>";
  web.sendHeader("Cache-Control","no-store");web.send(200,"text/html; charset=utf-8",page);
 });
 web.on("/wifi",HTTP_POST,[](){
  if(!apRequest()){web.send(403,"text/plain","Forbidden");return;}
  String ssid=web.arg("ssid"),password=web.arg("password");
  if(ssid.length()<1||ssid.length()>32||password.length()>63||(password.length()>0&&password.length()<8)){web.send(400,"text/plain; charset=utf-8","Проверьте имя сети и пароль (не менее 8 символов).");return;}
  prefs.putString("ssid",ssid);prefs.putString("wifi",password);
  web.send(200,"text/html; charset=utf-8","<meta charset='utf-8'><h2>Настройки сохранены</h2><p>Подключаемся к Wi-Fi. Вернитесь на предыдущую страницу и запишите код привязки. Затем вернитесь в домашнюю сеть и запустите поиск в программе.</p><a href='/'>Код привязки</a>");
  WiFi.begin(ssid.c_str(),password.c_str());
 });
 web.on("/pair",HTTP_POST,[](){
  if(!apOpen||pairingCode.length()!=6||++pairAttempts>8||web.arg("code")!=pairingCode){web.send(403,"text/plain; charset=utf-8","Код неверен или истёк. Откройте настройку физической кнопкой.");return;}
  web.sendHeader("Cache-Control","no-store");web.send(200,"application/json","{\"token\":\""+token+"\"}");pairingCode="";
 });
 web.on("/status",HTTP_GET,[](){
  if(!authorize())return;
  String json="{\"id\":\""+deviceId+"\",\"version\":\""+FIRMWARE_VERSION+"\",\"power\":"+(powerOn?"true":"false")+",\"brightness\":"+String(streaming()?brightness:nightBrightness)+",\"nightBrightness\":"+String(nightBrightness)+",\"count\":"+String(ledCount)+",\"streaming\":"+(streaming()?"true":"false")+",\"rmtReady\":"+(rmtReady?"true":"false")+",\"receivedFrames\":"+String(receivedFrames)+",\"sequence\":"+String(sequence)+",\"firstRgb\":["+String(colors[0])+","+String(colors[1])+","+String(colors[2])+"]}";
  web.send(200,"application/json",json);
 });
 web.on("/control",HTTP_POST,[](){
  if(!authorize())return;
  const char* required[]={"power","brightness","nightBrightness","count","session"};
  for(auto key:required)if(!web.hasArg(key)){web.send(400,"text/plain","Missing field");return;}
  int count=web.arg("count").toInt(),b=web.arg("brightness").toInt(),n=web.arg("nightBrightness").toInt();
  if(count<4||count>MAX_LEDS||b<0||b>100||n<0||n>100||(web.arg("power")!="0"&&web.arg("power")!="1")){web.send(400,"text/plain","Invalid value");return;}
  uint32_t session=strtoul(web.arg("session").c_str(),nullptr,10);
  if(session!=frameSession){frameSession=session;sequence=0;lastFrame=0;hadStream=false;}
  bool nextPower=web.arg("power")=="1";
  if(nextPower!=powerOn||b!=brightness||n!=nightBrightness||count!=ledCount)markDirty();
  powerOn=nextPower;brightness=b;nightBrightness=n;ledCount=count;
  web.send(200,"application/json","{\"ok\":true}");
 });
 web.onNotFound([](){web.send(404,"text/plain","Not found");});web.begin();
}
void discover() {
 int size=discovery.parsePacket();if(size<=0)return;
 char request[80];int n=discovery.read(request,sizeof(request)-1);if(n<0)return;request[n]=0;
 String value(request);if(!value.startsWith("ALDISC1 ")||value.length()!=24)return;
 String nonce=value.substring(8);for(int i=0;i<16;i++)if(!isxdigit(nonce[i]))return;
 String reply="AL1|"+nonce+"|"+deviceId+"|"+FIRMWARE_VERSION;
 discovery.beginPacket(discovery.remoteIP(),discovery.remotePort());discovery.print(reply);discovery.endPacket();
}
uint32_t read32(const uint8_t* p){return (uint32_t)p[0]|((uint32_t)p[1]<<8)|((uint32_t)p[2]<<16)|((uint32_t)p[3]<<24);}
void receiveFrame() {
 // Bound work per loop so the physical button and HTTP never starve.
 for(int attempt=0;attempt<4;attempt++){
  int size=frames.parsePacket();if(size<=0)return;
  if(size<48||size>(int)sizeof(packet)){while(frames.available())frames.read();continue;}
  if(frames.read(packet,size)!=size||memcmp(packet,"ALF1",4)!=0)continue;
  uint16_t count=packet[12]|((uint16_t)packet[13]<<8);
  if(count!=ledCount||size!=16+count*3+32||read32(packet+4)!=frameSession)continue;
  uint8_t signature[32];
  mbedtls_md_hmac(mbedtls_md_info_from_type(MBEDTLS_MD_SHA256),(const unsigned char*)token.c_str(),token.length(),packet,size-32,signature);
  uint8_t diff=0;for(int i=0;i<32;i++)diff|=signature[i]^packet[size-32+i];if(diff)continue;
  uint32_t next=read32(packet+8);if((int32_t)(next-sequence)<=0)continue;
  sequence=next;memcpy(colors,packet+16,count*3);lastFrame=millis();hadStream=true;receivedFrames++;
 }
}
void button() {
 int raw=digitalRead(BUTTON_PIN);uint32_t now=millis();
 if(raw!=buttonRaw){buttonRaw=raw;buttonChanged=now;}
 if(now-buttonChanged>35&&raw!=buttonStable){buttonStable=raw;if(raw==LOW){pressedAt=now;held=false;}else if(!held){powerOn=!powerOn;markDirty();}}
 if(buttonStable==LOW&&!held&&now-pressedAt>=5000){held=true;openProvisioning();}
}
void ARDUINO_ISR_ATTR encoderInterrupt() {
#if ENCODER_A_PIN >= 0 && ENCODER_B_PIN >= 0
 static DRAM_ATTR const int8_t table[]={0,-1,1,0,1,0,0,-1,-1,0,0,1,0,1,-1,0};
 int current=(digitalRead(ENCODER_A_PIN)<<1)|digitalRead(ENCODER_B_PIN);
 encoderDelta+=table[(encoderPrevious<<2)|current];encoderPrevious=current;
#endif
}
void encoder() {
#if ENCODER_A_PIN >= 0 && ENCODER_B_PIN >= 0
 noInterrupts();int delta=encoderDelta;encoderDelta=0;interrupts();encoderSteps+=delta;
 if(abs(encoderSteps)>=4){int step=encoderSteps/4*2;encoderSteps%=4;if(streaming())brightness=constrain(brightness+step,0,100);else nightBrightness=constrain(nightBrightness+step,0,100);markDirty();}
#endif
}
void serialMaintenance() {
 while(Serial.available()){
  char c=Serial.read();if(c=='\r')continue;
  if(c=='\n'){
   if(serialCommand=="SETUP")openProvisioning();
   else if(serialCommand=="CLOSESETUP"){closeProvisioning();Serial.println("ALSETUP closed");}
   else if(serialCommand=="STATUS")Serial.printf("ALSTATUS id=%s version=%s wifi=%d ip=%s power=%d brightness=%d count=%u rmt=%d\n",deviceId.c_str(),FIRMWARE_VERSION,WiFi.status()==WL_CONNECTED,WiFi.localIP().toString().c_str(),powerOn,streaming()?brightness:nightBrightness,ledCount,rmtReady);
   else if(serialCommand=="PAIRCODE"&&apOpen)Serial.println("ALPAIR "+pairingCode);
   else if(serialCommand.startsWith("WIFI ")){
    // Local USB provisioning only. Never echo credentials to logs or network replies.
    int space=serialCommand.indexOf(' ',5);String s=serialCommand.substring(5,space),p=serialCommand.substring(space+1);
    unsigned char ssid[33]={},password[64]={};size_t sl=0,pl=0;
    if(space<0||mbedtls_base64_decode(ssid,32,&sl,(const unsigned char*)s.c_str(),s.length())!=0||mbedtls_base64_decode(password,63,&pl,(const unsigned char*)p.c_str(),p.length())!=0||sl==0||(pl>0&&pl<8))Serial.println("ALWIFI invalid");
    else{prefs.putString("ssid",(const char*)ssid);prefs.putString("wifi",(const char*)password);WiFi.begin((const char*)ssid,(const char*)password);Serial.println("ALWIFI saved");memset(password,0,sizeof(password));}
   }
   serialCommand="";
  }else if(serialCommand.length()<192)serialCommand+=c;else serialCommand="";
 }
}
void draw() {
 if(!rmtReady||millis()-lastDraw<16)return;lastDraw=millis();bool live=streaming();int level=live?brightness:nightBrightness;size_t n=0;
 for(int i=0;i<MAX_LEDS;i++){
  uint8_t channel[LED_CHANNELS]={};
  if(powerOn&&i<ledCount){channel[CHANNEL_R]=(live?colors[i*3]:255)*level/100;channel[CHANNEL_G]=(live?colors[i*3+1]:143)*level/100;channel[CHANNEL_B]=(live?colors[i*3+2]:49)*level/100;}
  for(int c=0;c<LED_CHANNELS;c++)for(int b=7;b>=0;b--){bool one=channel[c]&(1<<b);symbols[n].level0=1;symbols[n].duration0=one?8:4;symbols[n].level1=0;symbols[n].duration1=one?4:8;n++;}
 }
 rmtWrite(LED_PIN,symbols,n,50);delayMicroseconds(300);
}
void setup() {
 Serial.begin(115200);Serial.println("ALBOOT " FIRMWARE_VERSION);pinMode(BUTTON_PIN,INPUT_PULLUP);prefs.begin("ambilight",false);Serial.println("ALBOOT preferences");
 uint64_t id=ESP.getEfuseMac();char buffer[13];snprintf(buffer,sizeof(buffer),"%04x%08lx",(uint16_t)(id>>32),(unsigned long)id);deviceId=String(buffer);
 token=prefs.getString("token","");if(token.length()!=32){token=randomHex();prefs.putString("token",token);}
 apPassword=prefs.getString("apkey","");if(apPassword.length()<12){apPassword=randomHex().substring(0,12);prefs.putString("apkey",apPassword);}
 powerOn=prefs.getBool("power",true);brightness=constrain(prefs.getUChar("bright",65),0,100);nightBrightness=constrain(prefs.getUChar("night",20),0,100);ledCount=constrain(prefs.getUShort("count",DEFAULT_LEDS),4,MAX_LEDS);
#if ENCODER_A_PIN >= 0 && ENCODER_B_PIN >= 0
 pinMode(ENCODER_A_PIN,INPUT_PULLUP);pinMode(ENCODER_B_PIN,INPUT_PULLUP);encoderPrevious=(digitalRead(ENCODER_A_PIN)<<1)|digitalRead(ENCODER_B_PIN);
 attachInterrupt(ENCODER_A_PIN,encoderInterrupt,CHANGE);attachInterrupt(ENCODER_B_PIN,encoderInterrupt,CHANGE);
#endif
 Serial.println("ALBOOT wifi");WiFi.persistent(false);WiFi.mode(WIFI_AP_STA);closeProvisioning();WiFi.setSleep(false);WiFi.setHostname(("ambilight-"+deviceId.substring(6)).c_str());
 Serial.println("ALBOOT wifi-credentials");String ssid=prefs.getString("ssid","");if(ssid.length())WiFi.begin(ssid.c_str(),prefs.getString("wifi","").c_str());else openProvisioning();
 Serial.println("ALBOOT rmt");rmtReady=rmtInit(LED_PIN,RMT_TX_MODE,RMT_MEM_NUM_BLOCKS_1,10000000);if(rmtReady)rmtSetEOT(LED_PIN,0);else Serial.println("Ошибка инициализации выхода ленты");
 discovery.begin(19446);frames.begin(19447);configureWeb();Serial.println("ALBOOT ready");
}
void loop() {
 button();encoder();serialMaintenance();web.handleClient();discover();receiveFrame();draw();
 if(hadStream&&!streaming()){hadStream=false;powerOn=false;markDirty();}
 if(dirty&&millis()-dirtyAt>1200)saveState();
 if(apOpen&&(int32_t)(millis()-apDeadline)>=0)closeProvisioning();
 if(WiFi.status()!=WL_CONNECTED&&millis()-lastReconnect>15000){lastReconnect=millis();WiFi.reconnect();}
 delay(1);
}
