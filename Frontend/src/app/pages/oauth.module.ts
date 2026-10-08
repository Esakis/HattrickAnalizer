import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { OAuthSetupComponent } from '../components/oauth-setup/oauth-setup.component';

@NgModule({
  declarations: [OAuthSetupComponent],
  imports: [CommonModule, FormsModule, TranslateModule, RouterModule.forChild([{ path: '', component: OAuthSetupComponent }])]
})
export class OAuthPageModule {}
